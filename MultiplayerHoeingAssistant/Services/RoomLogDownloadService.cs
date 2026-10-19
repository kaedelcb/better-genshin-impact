using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed record RoomLogMember(string Uid, string Name, bool Online);

public sealed record RoomLogMemberResult(string Uid, string Name, int BgiFiles, int AssistantFiles,
    int FailedFiles, string Message)
{
    public bool Complete => FailedFiles == 0 && BgiFiles > 0 && AssistantFiles > 0;
}

public sealed record RoomLogDownloadProgress(double Fraction, string Message);

public sealed record RoomLogDownloadResult(string OutputDirectory, bool Cancelled,
    IReadOnlyList<RoomLogMemberResult> Members);

/// <summary>
/// 使用既有房间连接依次收集成员日志；不修改单文件浏览器的选择、请求或连接。
/// 接收回调仅认领并排队，压缩文件写入、解压与结果清单均在后台执行。
/// </summary>
public sealed class RoomLogDownloadService
{
    private const int MaxFiles = 200;
    private const int MaxChunks = 400;
    private const int MaxChunkBase64Chars = 256 * 1024;
    private const long MaxRawBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan BetweenFiles = TimeSpan.FromMilliseconds(200);
    private static readonly Regex AssistantLogFileNameRegex = new(
        @"^assistant_runtime\.\d{4}-\d{2}-\d{2}(?:\.(?:s\d+|\d+))*\.log$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    private readonly SignalRClient _client;
    private readonly TimeSpan _timeout;
    private int _running;

    public RoomLogDownloadService(SignalRClient client, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<RoomLogDownloadResult> DownloadAsync(string roomName,
        IReadOnlyList<RoomLogMember> members, DateTime date, string outputParent,
        IProgress<RoomLogDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputParent);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("该房间日志下载仍在进行中");
        try
        {
            var snapshot = members.ToArray();
            return await Task.Run(() => DownloadCoreAsync(roomName, snapshot, date.Date,
                outputParent, progress, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<RoomLogDownloadResult> DownloadCoreAsync(string roomName, RoomLogMember[] members,
        DateTime date, string outputParent, IProgress<RoomLogDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(Path.GetFullPath(outputParent),
            $"房间-{SafeName(roomName, 40)}-{date:yyyy-MM-dd}-{DateTime.Now:HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var states = members.Select(m => new MemberState(m)).ToArray();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionLost = 0;
        var connectionGate = new object();
        var observingConnection = true;
        void ConnectionChanged(bool connected)
        {
            if (connected) return;
            lock (connectionGate)
            {
                if (!observingConnection) return;
                Interlocked.Exchange(ref connectionLost, 1);
                linked.Cancel();
            }
        }
        string StopReason() => Volatile.Read(ref connectionLost) != 0 ? "房间连接已断开，下载中止" : "已取消下载";

        _client.OnConnectionStateChanged += ConnectionChanged;
        try
        {
            if (!_client.IsConnected) ConnectionChanged(false);
            for (var i = 0; i < states.Length; i++)
            {
                linked.Token.ThrowIfCancellationRequested();
                var state = states[i];
                state.Started = true;
                if (!state.Member.Online)
                {
                    state.Messages.Add("成员离线，已跳过；未取得当天 BGI 和助手日志");
                    Report(progress, (i + 1d) / Math.Max(1, states.Length), $"{state.Member.Name}：离线，已跳过");
                    continue;
                }

                var directory = Path.Combine(outputDirectory,
                    $"成员-{i + 1:D2}-{SafeName(state.Member.Name, 40)}-{SafeName(state.Member.Uid, 48)}");
                try
                {
                    await DownloadMemberAsync(state, directory, date, (fraction, message) =>
                        Report(progress, (i + fraction) / Math.Max(1, states.Length), message),
                        StopReason, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    AppendUnfinishedTargets(state);
                    state.Messages.Add(StopReason());
                    throw;
                }
                catch (Exception ex)
                {
                    AppendUnfinishedTargets(state);
                    state.Messages.Add("成员日志收集失败：" + ex.Message);
                }
                AppendMissingKinds(state);
                Report(progress, (i + 1d) / Math.Max(1, states.Length), $"{state.Member.Name}：{state.Describe()}");
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            foreach (var state in states)
            {
                if (!state.Started) state.Messages.Add(StopReason() + "，尚未开始该成员");
                if (state.Started && !state.Messages.Any(m => m.StartsWith("未取得当天", StringComparison.Ordinal)))
                    AppendMissingKinds(state);
            }
        }
        finally
        {
            _client.OnConnectionStateChanged -= ConnectionChanged;
            lock (connectionGate) observingConnection = false;
        }

        var results = states.Select(s => s.ToResult()).ToArray();
        var cancelled = linked.IsCancellationRequested;
        var manifest = new StringBuilder()
            .AppendLine($"房间：{roomName}")
            .AppendLine($"日志日期：{date:yyyy-MM-dd}（按成员上报的文件修改日期筛选）")
            .AppendLine($"结束时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"状态：{(cancelled ? StopReason() : "收集结束，请核对各成员结果")}")
            .AppendLine($"成员快照：{members.Length} 人；BGI {results.Sum(r => r.BgiFiles)} 个，助手 {results.Sum(r => r.AssistantFiles)} 个，文件失败 {results.Sum(r => r.FailedFiles)} 个")
            .AppendLine("空列表可能代表未开启共享或没有可分享日志，协议无法区分；未取得日志不等于无日志。")
            .AppendLine();
        foreach (var result in results)
        {
            manifest.AppendLine($"{result.Name}（UID：{result.Uid}）")
                .AppendLine($"  BGI：{result.BgiFiles} 个；助手：{result.AssistantFiles} 个；文件失败：{result.FailedFiles} 个；{(result.Complete ? "两类当天日志均已下载" : "未完整取得两类当天日志")}")
                .AppendLine("  " + result.Message)
                .AppendLine();
        }

        // 不沿用已取消的 token：取消也必须写出已成功文件与未开始成员的真实结果。
        var manifestPath = Path.Combine(outputDirectory, "下载结果.txt");
        await File.WriteAllTextAsync(manifestPath, manifest.ToString(), Utf8NoBom).ConfigureAwait(false);
        Report(progress, cancelled ? 0 : 1,
            $"{(cancelled ? StopReason() : "房间日志收集结束")}，已保存下载结果.txt");
        return new RoomLogDownloadResult(outputDirectory, cancelled, results);
    }

    private async Task DownloadMemberAsync(MemberState state, string directory, DateTime date,
        Action<double, string> report, Func<string> stopReason, CancellationToken cancellationToken)
    {
        report(0, $"正在获取 {state.Member.Name} 的日志列表…");
        MemberLogFileDescriptor[] files;
        try
        {
            files = await RequestFilesAsync(state.Member.Uid, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            state.Messages.Add("获取列表失败：" + ex.Message);
            return;
        }

        if (files.Length == 0)
        {
            state.Messages.Add("收到空列表：对方可能未开启共享或没有可分享日志，无法区分");
            return;
        }

        var selected = files.Where(f => f.LastWrite.Date == date && IsAllowedLog(f.Name))
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length == 0)
        {
            state.Messages.Add($"列表中没有修改日期为 {date:yyyy-MM-dd} 的 BGI 或助手日志");
            return;
        }
        state.PendingFiles = selected.Length;
        Directory.CreateDirectory(directory);
        for (var i = 0; i < selected.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = selected[i];
            report((double)i / selected.Length, $"正在下载 {state.Member.Name}：{file.Name}（{i + 1}/{selected.Length}）");
            try
            {
                if (file.Size < 0 || file.Size > MaxRawBytes)
                    throw new InvalidDataException("文件大小无效或超过 200MiB 上限");
                await DownloadFileAsync(state.Member.Uid, file.Name, directory,
                    warning => state.Messages.Add(file.Name + "：" + warning), cancellationToken).ConfigureAwait(false);
                if (LogFileBrowser.BgiLogFileNameRegex.IsMatch(file.Name)) state.BgiFiles++;
                else state.AssistantFiles++;
                state.PendingFiles--;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                state.Messages.Add(file.Name + "：" + stopReason());
                throw;
            }
            catch (Exception ex)
            {
                state.FailedFiles++;
                state.PendingFiles--;
                state.Messages.Add(file.Name + "：下载失败，" + ex.Message);
            }
            // 对端在最后块的发送 ACK 后才释放单文件 busy；同时低于忙标记 5 次/秒的限流。
            if (i < selected.Length - 1)
                await Task.Delay(BetweenFiles, cancellationToken).ConfigureAwait(false);
        }
        if (state.BgiFiles > 0 && state.AssistantFiles > 0 && state.FailedFiles == 0)
            state.Messages.Add("当天 BGI 和助手日志均已下载");
    }

    private async Task<MemberLogFileDescriptor[]> RequestFilesAsync(string uid, CancellationToken cancellationToken)
    {
        CheckConnection();
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<FileListReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        void FileList(MemberLogFileList list)
        {
            if (list.RequestId != requestId || list.Uid != uid) return;
            if (list.Files == null)
            {
                completion.TrySetResult(new FileListReply([], "应答缺少文件列表，不能判断日志是否存在"));
                return;
            }
            if (list.Files.Count > MaxFiles)
            {
                completion.TrySetResult(new FileListReply([], "文件列表超过协议 200 项上限"));
                return;
            }
            completion.TrySetResult(new FileListReply(list.Files.Where(f => f != null).Select(f => new MemberLogFileDescriptor
            {
                Name = f.Name ?? "", Size = f.Size, LastWrite = f.LastWrite
            }).ToArray(), null));
        }
        _client.OnMemberLogFileList += FileList;
        try
        {
            await SendAsync(() => _client.RequestMemberLogFilesAsync(uid, requestId), cancellationToken).ConfigureAwait(false);
            try
            {
                var reply = await completion.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
                if (reply.Error != null) throw new InvalidDataException(reply.Error);
                return reply.Files;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("列表请求超时；对方可能离线、未共享、拒绝或版本不支持，不能判断为无日志");
            }
        }
        finally
        {
            _client.OnMemberLogFileList -= FileList;
        }
    }

    private async Task DownloadFileAsync(string uid, string fileName, string directory,
        Action<string> warning, CancellationToken cancellationToken)
    {
        CheckConnection();
        var requestId = Guid.NewGuid().ToString("N");
        var compressedPath = Path.Combine(directory, fileName + "." + requestId + ".gzip.part");
        var temporaryPath = Path.Combine(directory, fileName + "." + requestId + ".part");
        var targetPath = Path.Combine(directory, fileName);
        var compressedCreated = false;
        var temporaryCreated = false;
        var queue = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(MaxChunks)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        var gate = new object();
        var expectedSeq = 0;
        var totalChunks = -1;
        var terminal = false;
        void FileChunk(MemberLogFileChunk chunk)
        {
            if (chunk.RequestId != requestId || chunk.Uid != uid || chunk.FileName != fileName) return;
            lock (gate)
            {
                if (terminal) return;
                string? error = null;
                if (chunk.TotalChunks == 0)
                    error = "对方正忙、拒绝共享或文件超过 200MiB 上限";
                else if (chunk.TotalChunks < 1 || chunk.TotalChunks > MaxChunks)
                    error = "分块总数超出协议上限";
                else if (totalChunks != -1 && totalChunks != chunk.TotalChunks)
                    error = "分块总数前后不一致";
                else if (chunk.Seq != expectedSeq || chunk.Seq >= chunk.TotalChunks)
                    error = "分块乱序、重复或缺失";
                else if (chunk.Done != (chunk.Seq == chunk.TotalChunks - 1))
                    error = "分块结束标记与序号不一致";
                else if (string.IsNullOrEmpty(chunk.ChunkBase64) || chunk.ChunkBase64.Length > MaxChunkBase64Chars)
                    error = "分块内容为空或超过协议上限";
                if (error != null)
                {
                    terminal = true;
                    queue.Writer.TryWrite(new Chunk(0, 0, "", true, error));
                    queue.Writer.TryComplete();
                    return;
                }
                totalChunks = chunk.TotalChunks;
                expectedSeq++;
                if (!queue.Writer.TryWrite(new Chunk(chunk.Seq, chunk.TotalChunks, chunk.ChunkBase64, chunk.Done)))
                {
                    terminal = true;
                    queue.Writer.TryComplete();
                }
                else if (chunk.Done)
                {
                    terminal = true;
                    queue.Writer.TryComplete();
                }
            }
        }

        _client.OnMemberLogFileChunk += FileChunk;
        try
        {
            await SendAsync(() => _client.RequestMemberLogDownloadAsync(uid, requestId, fileName), cancellationToken)
                .ConfigureAwait(false);
            var received = 0;
            using (var compressed = new FileStream(compressedPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                compressedCreated = true;
                while (true)
                {
                    Chunk chunk;
                    using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idleTimeout.CancelAfter(_timeout);
                    try { chunk = await queue.Reader.ReadAsync(idleTimeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"超过 {_timeout.TotalSeconds:0.#} 秒没有收到有效新分块，文件未完成");
                    }
                    catch (ChannelClosedException ex)
                    {
                        throw new InvalidDataException("分块不完整，文件未完成", ex);
                    }
                    if (chunk.Error != null) throw new InvalidDataException(chunk.Error);
                    byte[] decoded;
                    try { decoded = Convert.FromBase64String(chunk.Base64); }
                    catch (FormatException ex) { throw new InvalidDataException("分块 Base64 内容无效", ex); }
                    if (decoded.Length == 0) throw new InvalidDataException("分块解码后为空");
                    await compressed.WriteAsync(decoded, cancellationToken).ConfigureAwait(false);
                    received++;
                    if (!chunk.Done) continue;
                    if (received != chunk.Total) throw new InvalidDataException("分块未全部收齐");
                    await compressed.FlushAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
            }

            // 到齐后不再认领该请求；迟到或别的下载回包不会进入落盘过程。
            _client.OnMemberLogFileChunk -= FileChunk;
            using (var input = new FileStream(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                // GZipStream 对某些截断输入会返回 EOF；核对头部及 trailer，不能把半个 gzip 当完整日志。
                if (input.Length < 18) throw new InvalidDataException("gzip 文件不完整");
                var header = new byte[4];
                await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                if (header[0] != 0x1f || header[1] != 0x8b || header[2] != 8 || (header[3] & 0xe0) != 0)
                    throw new InvalidDataException("gzip 文件头无效");
                input.Position = input.Length - 8;
                var trailer = new byte[8];
                await input.ReadExactlyAsync(trailer, cancellationToken).ConfigureAwait(false);
                var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
                var expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(4));
                input.Position = 0;
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                temporaryCreated = true;
                var buffer = new byte[64 * 1024];
                long written = 0;
                var crc = uint.MaxValue;
                while (true)
                {
                    var count = await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    written += count;
                    if (written > MaxRawBytes) throw new InvalidDataException("解压后超过 200MiB 上限");
                    for (var n = 0; n < count; n++) crc = Crc32Table[(crc ^ buffer[n]) & 0xff] ^ (crc >> 8);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                if ((uint)written != expectedSize || ~crc != expectedCrc)
                    throw new InvalidDataException("gzip 长度或校验值不匹配，文件可能截断或损坏");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        finally
        {
            _client.OnMemberLogFileChunk -= FileChunk;
            lock (gate) { terminal = true; queue.Writer.TryComplete(); }
            if (compressedCreated) DeletePart(compressedPath, warning);
            if (temporaryCreated) DeletePart(temporaryPath, warning);
        }
    }

    private async Task SendAsync(Func<Task> send, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckConnection();
        var task = send();
        try { await task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false); }
        finally
        {
            // 网络调用不可取消，但它不写文件；显式观察之后的异常，所有磁盘操作仍由本任务等待到终态。
            if (!task.IsCompleted)
                _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        CheckConnection();
    }

    private void CheckConnection()
    {
        if (!_client.IsConnected) throw new IOException("房间连接已断开");
        if (_client.LogFileUnsupported) throw new InvalidOperationException("服务端拒绝或不支持日志文件下载");
    }

    private static bool IsAllowedLog(string name) => !string.IsNullOrEmpty(name)
        && MemberLogShareService.FileNameRegex.IsMatch(name)
        && (LogFileBrowser.BgiLogFileNameRegex.IsMatch(name) || AssistantLogFileNameRegex.IsMatch(name));

    private static void AppendMissingKinds(MemberState state)
    {
        if (state.BgiFiles == 0 && state.AssistantFiles == 0)
            state.Messages.Add("未取得当天 BGI 和助手日志");
        else if (state.BgiFiles == 0) state.Messages.Add("未取得当天 BGI 日志");
        else if (state.AssistantFiles == 0) state.Messages.Add("未取得当天助手日志");
    }

    private static void AppendUnfinishedTargets(MemberState state)
    {
        if (state.PendingFiles == 0) return;
        state.FailedFiles += state.PendingFiles;
        state.Messages.Add($"{state.PendingFiles} 个选定的当天日志尚未下载，计入未完成文件");
        state.PendingFiles = 0;
    }

    private static string SafeName(string? value, int maximumLength)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string((value ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.', ' ');
        return string.IsNullOrEmpty(result) ? "未命名" : result[..Math.Min(result.Length, maximumLength)];
    }

    private static void DeletePart(string path, Action<string> warning)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { warning("临时文件未能清除：" + Path.GetFileName(path) + "；" + ex.Message); }
    }

    private static void Report(IProgress<RoomLogDownloadProgress>? progress, double fraction, string message)
        => progress?.Report(new RoomLogDownloadProgress(Math.Clamp(fraction, 0, 1), message));

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var value = n;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            table[n] = value;
        }
        return table;
    }

    private sealed record FileListReply(MemberLogFileDescriptor[] Files, string? Error);
    private sealed record Chunk(int Seq, int Total, string Base64, bool Done, string? Error = null);

    private sealed class MemberState(RoomLogMember member)
    {
        public RoomLogMember Member { get; } = member;
        public bool Started;
        public int BgiFiles;
        public int AssistantFiles;
        public int FailedFiles;
        public int PendingFiles;
        public List<string> Messages { get; } = [];
        public string Describe() => $"BGI {BgiFiles} 个，助手 {AssistantFiles} 个，文件失败 {FailedFiles} 个";
        public RoomLogMemberResult ToResult() => new(Member.Uid, Member.Name, BgiFiles, AssistantFiles,
            FailedFiles, string.Join("；", Messages));
    }
}
