using System.Text;
using System.Text.Json;

namespace BgiCoordinatorServer.Services;

/// <summary>逐条解析、有限内存外排；请求期间追加的记录不进入既定长度快照。</summary>
internal static class RoomLogHistoryExport
{
    public static async Task<RoomLogRangeDownload?> CreateAsync(string directory, string code, RoomLogRange range,
        RoomLogHistorySnapshot snapshot, CancellationToken cancellationToken, RoomLogSelection? selection = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 卡片有确证的批次/会话身份，直接读对应文件；只有旧版合并才需要恢复历史关联。
        if (selection is null) snapshot = RoomLogLegacyHistory.AddLinks(snapshot, cancellationToken);
        var links = snapshot.Links.Where(link =>
            (selection?.SessionId is not null ? link.GameCode == code : link.ControlCode == code)
            && (selection?.BatchId is null || link.BatchId == selection.BatchId)
            && (selection?.SessionId is null || link.SessionId == selection.SessionId)).ToArray();
        if (selection is { Direct: false } && links.Length == 0) return null;
        var sources = selection is { Direct: true }
            ? new HashSet<string>([code], StringComparer.Ordinal)
            : links.Select(link => link.GameCode).Concat(selection is null ? new[] { code } : [])
                .ToHashSet(StringComparer.Ordinal);
        var files = snapshot.Files.Where(file => sources.Contains(file.Code) &&
            file.Date >= range.FirstDate && file.Date <= range.LastDate).OrderBy(file => file.Date).ToArray();
        if (files.Any(file => file.Incomplete))
            throw new IOException("所选日期的房间日志保存曾失败，无法提供完整下载。");
        var warnings = snapshot.Warnings.Where(warning => warning.Code == code || warning.Code == "*")
            .Select(warning => warning.Message).ToHashSet(StringComparer.Ordinal);
        warnings.Add("仅包含已保存且可证明属于本房间的服务器日志；旧版未保存的日志无法补回，未确定的旧会话关联不会猜测归并。");
        var scratch = Path.Combine(directory, ".downloads", Guid.NewGuid().ToString("N"));
        var temporaryFiles = new HashSet<string>(StringComparer.Ordinal);
        FileStream? body = null;
        try
        {
            Directory.CreateDirectory(scratch);
            var runs = new List<string>();
            var chunk = new List<RoomLogHistoryRecord>();
            long chunkBytes = 0;
            foreach (var file in files)
            {
                foreach (var record in RoomLogArchiveReader.Read(file, cancellationToken))
                {
                    var local = record.TimestampUtc.AddHours(8);
                    if (local < range.StartInclusive || local >= range.EndExclusive) continue;
                    if (selection is { Direct: false })
                    {
                        if (!Belongs(record, links)) continue;
                    }
                    else if (file.Code != code && !Belongs(record, links)) continue;
                    chunk.Add(record);
                    chunkBytes += (record.Header.Length + record.Message.Length) * 2L;
                    if (chunk.Count >= 1024 || chunkBytes >= 4 * 1024 * 1024)
                    {
                        runs.Add(await SpillAsync(chunk, scratch, temporaryFiles, cancellationToken));
                        chunk.Clear();
                        chunkBytes = 0;
                    }
                }
            }
            if (chunk.Count != 0) runs.Add(await SpillAsync(chunk, scratch, temporaryFiles, cancellationToken));
            if (runs.Count == 0) return null;
            // 每次最多打开32个run，任意长时间段不会耗尽文件句柄。
            while (runs.Count > 32)
            {
                var next = new List<string>();
                foreach (var group in runs.Chunk(32))
                {
                    var path = NewTemporaryPath(scratch, temporaryFiles);
                    await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
                    foreach (var record in Merge(group, cancellationToken))
                        await writer.WriteLineAsync(JsonSerializer.Serialize(record).AsMemory(), cancellationToken);
                    next.Add(path);
                }
                foreach (var path in runs) { File.Delete(path); temporaryFiles.Remove(path); }
                runs = next;
            }
            var bodyPath = NewTemporaryPath(scratch, temporaryFiles);
            body = new FileStream(bodyPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            long count = 0;
            DateTime? first = null, last = null;
            var actualSources = new HashSet<string>(StringComparer.Ordinal);
            await using (var writer = new StreamWriter(body, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
            {
                string? lastEvent = null;
                DateTime? lastEventTime = null;
                foreach (var record in Merge(runs, cancellationToken))
                {
                    actualSources.Add(record.SourceCode);
                    // 同一事件可能同时归档到控制与游戏房间。只有可靠event id才去重，保留旧版重复原证据。
                    if (record.EventId is not null && record.EventId == lastEvent && record.TimestampUtc == lastEventTime)
                        continue;
                    lastEvent = record.EventId;
                    lastEventTime = record.TimestampUtc;
                    await writer.WriteLineAsync(record.Header.AsMemory(), cancellationToken);
                    await writer.WriteLineAsync($"[来源房间 {record.SourceCode}；会话 {record.SessionId ?? "旧版未标识"}]".AsMemory(), cancellationToken);
                    await writer.WriteAsync(record.Message.AsMemory(), cancellationToken);
                    if (!record.Message.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken);
                    await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken);
                    count++;
                    first ??= record.TimestampUtc;
                    last = record.TimestampUtc;
                }
                await writer.FlushAsync(cancellationToken);
            }
            body.Position = 0;
            var download = new RoomLogRangeDownload(code, range, body,
                new RoomLogDownloadMetadata(count, first, last, actualSources.Order(StringComparer.Ordinal).ToArray(),
                    warnings.Order(StringComparer.Ordinal).ToArray()),
                selection?.BatchId is { } batchId ? "锄地批次 " + batchId + " 的世界日志"
                    : selection?.SessionId is { } sessionId ? "世界房间会话 " + sessionId
                    : selection?.Direct == true ? "仅此房间号自身日志" : null);
            body = null;
            temporaryFiles.Remove(bodyPath);
            return download;
        }
        finally
        {
            if (body is not null) await body.DisposeAsync();
            foreach (var path in temporaryFiles)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            // 成功下载仍持有DeleteOnClose正文；由download.DisposeAsync删除正文后删空目录。
            try { Directory.Delete(scratch); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Belongs(RoomLogHistoryRecord record, RoomLogLink[] links) => links.Any(link =>
        link.GameCode == record.SourceCode &&
        (record.SessionId is not null
            ? record.SessionId == link.SessionId
            : link.Source == "legacyRoundOrder" && record.EventId is null && record.TimestampUtc >= link.StartedUtc &&
              (link.EndedUtc is null || record.TimestampUtc < link.EndedUtc)));

    private static string NewTemporaryPath(string scratch, HashSet<string> temporaryFiles)
    {
        var path = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".tmp");
        temporaryFiles.Add(path);
        return path;
    }

    private static async Task<string> SpillAsync(List<RoomLogHistoryRecord> chunk, string scratch,
        HashSet<string> temporaryFiles, CancellationToken cancellationToken)
    {
        chunk.Sort(Compare);
        var path = NewTemporaryPath(scratch, temporaryFiles);
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var record in chunk)
            await writer.WriteLineAsync(JsonSerializer.Serialize(record).AsMemory(), cancellationToken);
        return path;
    }

    private static int Compare(RoomLogHistoryRecord left, RoomLogHistoryRecord right)
    {
        var timestamp = left.TimestampUtc.CompareTo(right.TimestampUtc);
        if (timestamp != 0) return timestamp;
        var identity = StringComparer.Ordinal.Compare(left.SortId, right.SortId);
        if (identity != 0) return identity;
        var source = StringComparer.Ordinal.Compare(left.SourceCode, right.SourceCode);
        return source != 0 ? source : left.Ordinal.CompareTo(right.Ordinal);
    }

    private static IEnumerable<RoomLogHistoryRecord> Merge(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var readers = new List<StreamReader>();
        var queue = new PriorityQueue<(RoomLogHistoryRecord Record, StreamReader Reader), RoomLogHistoryRecord>(
            Comparer<RoomLogHistoryRecord>.Create(Compare));
        try
        {
            foreach (var path in paths)
            {
                var reader = new StreamReader(path, Encoding.UTF8);
                readers.Add(reader);
                if (ReadRecord(reader) is { } record) queue.Enqueue((record, reader), record);
            }
            while (queue.TryDequeue(out var item, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item.Record;
                if (ReadRecord(item.Reader) is { } record) queue.Enqueue((record, item.Reader), record);
            }
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
    }

    private static RoomLogHistoryRecord? ReadRecord(StreamReader reader)
    {
        var line = reader.ReadLine();
        return line is null ? null : JsonSerializer.Deserialize<RoomLogHistoryRecord>(line) ??
            throw new IOException("下载排序临时记录损坏。");
    }
}
