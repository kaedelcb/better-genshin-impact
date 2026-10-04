using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Services;

public enum MigrationOperationPhase { Reference, Activate, Undo, Restore, DeleteAdded, RemoveOwnedDirectory }
public enum MigrationOperationState { Prepared, Applied, Cancelled }

public sealed record MigrationOperationIntent(string OperationId, int Sequence, MigrationOperationPhase Phase,
    string Path, MigrationFileVersion Input, string? ExpectedOutputSha256, string? OutputBlob,
    string? PredecessorOperationId, IReadOnlyList<MigrationDirectoryEffect> Directories)
{
    public Guid? KernelTransactionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, MigrationFileVersion>? ParentInputs { get; init; }
}

public sealed record MigrationOperationResolution(string TransactionId, string OperationId, int Sequence,
    MigrationOperationState State, string IntentSha256, MigrationFileVersion Input, MigrationFileVersion Output,
    IReadOnlyList<MigrationDirectoryEffect> Directories, string? PreviousResolutionSha256,
    string? WitnessId, bool MutationPerformed, string Reason, string Integrity = "");

public sealed record MigrationJournalEntry(MigrationOperationIntent Intent, MigrationOperationState State,
    string? ResolutionPath = null, string? ResolutionSha256 = null);

public sealed record MigrationPhaseAuthorization(string ManifestBytesSha256, int PrefixCount,
    string AppliedPrefixDigest, string Path, string PredecessorOperationId);

public sealed record MigrationJournalDocument(string TransactionId, MigrationStage Stage,
    MigrationStage LastStableStage, MigrationStage? PendingStage, bool RegistrationFrozen,
    IReadOnlyList<MigrationJournalEntry> Operations, string? StageWitnessId = null,
    string? BlockedReason = null, int SchemaVersion = 2, string Integrity = "")
{
    public MigrationPhaseAuthorization? PhaseAuthorization { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MigrationFrozenDeclaration? Declaration { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MigrationBaselineObservation>? BaselineObservations { get; init; }
}

public sealed record MigrationFrozenReference(MigrationReferenceWriteTarget Target, MigrationFileVersion Input, string OutputSha256);
public sealed record MigrationFrozenDeclaration(string TransactionId, string SnapshotId, string SnapshotManifestHash,
    IReadOnlyList<ChangeRecord> ChangedFiles, IReadOnlyList<MigrationFrozenReference> References)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MigrationRootEntry>? BaselineEntries { get; init; }
}
public sealed record MigrationBaselineObservation(string Path, MigrationFileVersion Observed, string BaselineHash,
    string? LatestAppliedOperationId, string WitnessId, bool MutationPerformed = false,
    string Reason = "already_exact_baseline_no_write");
public sealed record MigrationAuthorityView(MigrationJournalDocument Document, string JournalBytesSha256,
    IReadOnlyDictionary<string, MigrationOperationResolution> Resolutions, IReadOnlyDictionary<string, byte[]> Outputs,
    string ResolutionChainDigest, string AppliedChainDigest);

/// <summary>Immutable intents survive cancellation. Resolved prefix and actual data chain are distinct.</summary>
public sealed class MigrationOperationJournal
{
    private readonly string _root;
    private readonly IMigrationVersionStore _store;
    private readonly string _artifactPrefix;
    private readonly Func<MigrationAuthorityView?, MigrationAuthorityView, IReadOnlyList<MigrationArtifactWrite>>? _companion;
    private const string DocumentPath = "operation-journal.json";
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public MigrationOperationJournal(string artifactRoot, IMigrationVersionStore store, string artifactPrefix = "",
        Func<MigrationAuthorityView?, MigrationAuthorityView, IReadOnlyList<MigrationArtifactWrite>>? companion = null)
    {
        _root = Path.GetFullPath(artifactRoot);
        _store = store;
        _artifactPrefix = artifactPrefix.Replace('\\', '/').TrimEnd('/');
        if (_artifactPrefix.Length != 0) _ = WindowsTxfMigrationVersionStore.Resolve(_root, _artifactPrefix);
        _companion = companion;
    }

    public MigrationJournalDocument Create(string transactionId, MigrationStage stage = MigrationStage.SnapshotReady)
    {
        var document = Seal(new MigrationJournalDocument(transactionId, stage, stage, null, false, []));
        _store.WriteArtifacts(BuildPublication(null, document));
        return document;
    }

    public MigrationJournalDocument Load() => ReadAuthority().Document;

    public static string EmptyChainDigest => Hash(Array.Empty<MigrationJournalEntry>());

    public (string OperationId, MigrationFileVersion Output)? LastAppliedFile(string relativePath)
    {
        var document = Load();
        foreach (var entry in document.Operations.Where(e => e.State == MigrationOperationState.Applied).Reverse())
        {
            var resolution = JsonSerializer.Deserialize<MigrationOperationResolution>(File.ReadAllBytes(Full(entry.ResolutionPath!)), Options)
                ?? throw new InvalidDataException("migration_resolution_null");
            if (string.Equals(entry.Intent.Path, relativePath, StringComparison.OrdinalIgnoreCase)) return (entry.Intent.OperationId, resolution.Output);
            var directory = resolution.Directories.SingleOrDefault(d => string.Equals(d.Path, relativePath, StringComparison.OrdinalIgnoreCase));
            if (directory is not null) return (entry.Intent.OperationId, directory.Output);
        }
        return null;
    }

    public IReadOnlyList<MigrationDirectoryEffect> AppliedDirectories()
    {
        var document = Load();
        return document.Operations.Where(e => e.State == MigrationOperationState.Applied)
            .SelectMany(e => JsonSerializer.Deserialize<MigrationOperationResolution>(File.ReadAllBytes(Full(e.ResolutionPath!)), Options)!.Directories)
            .GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(d => d.Path.Count(c => c == '/')).ThenByDescending(d => d.Path.Length).ToArray();
    }

    public IReadOnlyList<MigrationOperationResolution> AppliedEffects()
    {
        var document = Load();
        return document.Operations.Where(e => e.State == MigrationOperationState.Applied)
            .Select(e => JsonSerializer.Deserialize<MigrationOperationResolution>(File.ReadAllBytes(Full(e.ResolutionPath!)), Options)!).ToArray();
    }

    public MigrationVersionMutationResult ExecutePreparedDelete(string relativePath, MigrationFileVersion input,
        string? predecessorOperationId = null)
    {
        var parents = ExpectedParents(relativePath);
        var id = Guid.NewGuid().ToString("N");
        Guid? kernelTransaction = null;
        var phase = input.Kind == MigrationEntryKind.Directory ? MigrationOperationPhase.RemoveOwnedDirectory : MigrationOperationPhase.DeleteAdded;
        void PrepareDelete() => Prepare(new(id, Load().Operations.Count + 1, phase, relativePath,
            input, null, null, predecessorOperationId, []) { KernelTransactionId = kernelTransaction, ParentInputs = parents });
        if (input.Kind == MigrationEntryKind.Directory)
        {
            return _store.RemoveDirectory(relativePath, input, output => AppliedArtifacts(id, output, []),
                transactionCreated: guid => { kernelTransaction = guid; PrepareDelete(); }, parents: parents);
        }
        return _store.ExecuteFile(relativePath, input, (_, _, _) =>
        {
            PrepareDelete();
            return new(null, true, (output, directories) => AppliedArtifacts(id, output, directories));
        }, parents: parents, transactionCreated: guid => kernelTransaction = guid);
    }

    /// <summary>Reads the journal, receipts and output blobs from one caller-selected immutable root.</summary>
    public (string Digest, IReadOnlyDictionary<string, byte[]> Files) ReadValidatedEvidence()
    {
        var document = Load();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        { [DocumentPath] = File.ReadAllBytes(Full(DocumentPath)) };
        foreach (var entry in document.Operations)
        {
            if (entry.Intent.OutputBlob is { } blob)
            {
                var bytes = File.ReadAllBytes(Full(blob));
                if (MigrationFileVersion.Hash(bytes) != entry.Intent.ExpectedOutputSha256)
                    throw new InvalidDataException("migration_output_blob_integrity_invalid");
                if (!files.TryAdd(blob, bytes)) throw new InvalidDataException("migration_evidence_path_reused");
            }
            if (entry.ResolutionPath is { } receipt)
                if (!files.TryAdd(receipt, File.ReadAllBytes(Full(receipt))))
                    throw new InvalidDataException("migration_evidence_path_reused");
        }
        return (Hash(document.Operations), files);
    }

    public MigrationJournalDocument Prepare(MigrationOperationIntent intent, byte[]? outputBytes = null)
    {
        var before = ReadAuthority();
        var document = before.Document;
        ValidatePreparedPhase(document, intent.Phase);
        if (document.Operations.Any(e => e.State == MigrationOperationState.Prepared))
            throw new InvalidOperationException("migration_unresolved_prepared_tail");
        if (intent.Sequence != document.Operations.Count + 1 ||
            document.Operations.Any(e => e.Intent.OperationId == intent.OperationId))
            throw new InvalidOperationException("migration_operation_identity_reused");
        ValidateIntent(intent);
        var entries = document.Operations.Append(new MigrationJournalEntry(intent, MigrationOperationState.Prepared)).ToArray();
        var next = Seal(document with { RegistrationFrozen = true, Operations = entries });
        if (outputBytes is null) _store.WriteArtifacts(BuildPublication(before, next));
        else
        {
            if (intent.OutputBlob is null || MigrationFileVersion.Hash(outputBytes) != intent.ExpectedOutputSha256)
                throw new InvalidDataException("migration_prepared_blob_hash_mismatch");
            _store.WriteArtifacts(BuildPublication(before, next, [new(intent.OutputBlob, outputBytes)]));
        }
        return next;
    }

    public MigrationJournalDocument BeginActivationPhase(string manifestBytesSha256, string path)
    {
        var document = Load();
        var predecessor = document.Operations.LastOrDefault(e => e.State == MigrationOperationState.Applied &&
            string.Equals(e.Intent.Path, path, StringComparison.OrdinalIgnoreCase));
        if (!IsHash(manifestBytesSha256) || document.PendingStage is not null ||
            document.Stage is not (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating) ||
            document.Declaration is not null && document.Stage != MigrationStage.ReferenceUpdating ||
            document.Operations.Any(e => e.State == MigrationOperationState.Prepared) ||
            predecessor is null || predecessor.Intent.Phase != MigrationOperationPhase.Reference)
            throw new InvalidOperationException("migration_activation_phase_not_authorized");
        var next = Seal(document with { PendingStage = MigrationStage.Activated,
            PhaseAuthorization = new(manifestBytesSha256, document.Operations.Count, Hash(document.Operations),
                path, predecessor.Intent.OperationId) });
        Validate(next);
        Publish(next, document);
        return next;
    }

    /// <summary>
    /// Binds a pure transform to the caller's previously established input version.
    /// It never adopts an observed foreign version. Prepared/blob are durable before data;
    /// data, Applied receipt and journal advancement commit in the same kernel transaction.
    /// The caller must separately verify a fresh whole-root witness before stage success.
    /// </summary>
    public MigrationVersionMutationResult ExecutePreparedFile(string relativePath, MigrationFileVersion input,
        MigrationOperationPhase phase, Func<byte[]?, byte[]> transform,
        string? predecessorOperationId = null,
        IReadOnlyDictionary<string, MigrationFileVersion>? parents = null, Action<string>? fault = null)
    {
        if (phase is MigrationOperationPhase.DeleteAdded or MigrationOperationPhase.RemoveOwnedDirectory)
            throw new ArgumentException("migration_file_transform_cannot_delete", nameof(phase));
        parents ??= ExpectedParents(relativePath);
        var operationId = Guid.NewGuid().ToString("N");
        Guid? kernelTransaction = null;
        return _store.ExecuteFile(relativePath, input, (bytes, observed, directories) =>
        {
            // ExecuteFile has already compared identity and bytes on this same handle.
            var output = (byte[])(transform(observed.Exists ? bytes : null)
                ?? throw new InvalidOperationException("migration_transform_output_missing")).Clone();
            var document = Load();
            var intent = new MigrationOperationIntent(operationId, document.Operations.Count + 1, phase,
                relativePath, input, MigrationFileVersion.Hash(output), "outputs/" + operationId + ".bin",
                predecessorOperationId, directories.ToArray()) { KernelTransactionId = kernelTransaction,
                ParentInputs = parents?.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) };
            Prepare(intent, output);
            return new(output, false, (actual, created) => AppliedArtifacts(operationId, actual, created));
        }, parents, fault, transactionCreated: guid => kernelTransaction = guid);
    }

    public MigrationJournalDocument ResolveTerminatedPreparedTail(MigrationControlledRootWitness? rootWitness = null)
    {
        var document = Load();
        var tail = document.Operations.LastOrDefault(e => e.State == MigrationOperationState.Prepared);
        if (tail is null) return document;
        if (tail.Intent.KernelTransactionId is not { } transactionId ||
            !WindowsTxfMigrationVersionStore.IsProducerTransactionTerminated(transactionId))
            throw new InvalidOperationException("migration_prepared_producer_not_proven_terminated");
        if (document.Declaration is not null && (rootWitness is null ||
            rootWitness.TransactionId != document.TransactionId || rootWitness.AppliedChainDigest != ReadAuthority().AppliedChainDigest ||
            rootWitness.ResolutionChainDigest != ReadAuthority().ResolutionChainDigest || rootWitness.LeaseId == Guid.Empty ||
            rootWitness.WriterDomain != "registered-root-authority" || rootWitness.SuccessfulStage is not null ||
            rootWitness.Integrity != Hash(rootWitness with { Integrity = "" })))
            throw new InvalidDataException("migration_resolution_fresh_root_missing");
        var observed = _store.ReadVersion(tail.Intent.Path, tail.Intent.Input.Kind);
        if (!tail.Intent.Input.Matches(observed)) throw new InvalidOperationException("migration_prepared_input_drifted");
        foreach (var parent in tail.Intent.ParentInputs ?? new Dictionary<string, MigrationFileVersion>())
            if (!parent.Value.Matches(_store.ReadVersion(parent.Key, MigrationEntryKind.Directory)))
                throw new InvalidOperationException("migration_prepared_parent_drifted");
        var witness = new { document.TransactionId, tail.Intent.OperationId, KernelTransactionId = transactionId,
            ObservedInput = observed, ProducerTerminated = true, RootWitness = rootWitness, CapturedAtUtc = DateTimeOffset.UtcNow };
        var path = "resolutions/" + tail.Intent.OperationId + ".no-effect-witness.json";
        var bytes = Encode(witness);
        _store.WriteArtifacts([new(StorePath(path), bytes)]);
        return CancelPrepared(tail.Intent.OperationId, observed, MigrationFileVersion.Hash(bytes), true,
            "prepared_input_unchanged_after_native_producer_terminated");
    }

    /// <summary>Returned writes must be committed by the same transaction as the data mutation.</summary>
    public IReadOnlyList<MigrationArtifactWrite> AppliedArtifacts(string operationId, MigrationFileVersion output,
        IReadOnlyList<MigrationDirectoryEffect> directories)
    {
        var before = ReadAuthority();
        var document = before.Document;
        var tail = PreparedTail(document, operationId);
        ValidateOutput(tail.Intent, output);
        if (tail.Intent.ExpectedOutputSha256 is { } expected && output.Sha256 != expected)
            throw new InvalidDataException("migration_applied_output_differs_from_intent");
        var resolution = Seal(new MigrationOperationResolution(document.TransactionId, operationId, tail.Intent.Sequence,
            MigrationOperationState.Applied, Hash(tail.Intent), tail.Intent.Input, output, directories,
            LastResolution(document), null, true, "applied"));
        return ResolutionArtifacts(before, tail, resolution);
    }

    public MigrationJournalDocument CancelPrepared(string operationId, MigrationFileVersion observedInput,
        string freshWitnessId, bool previousProducerAndTransactionTerminated, string reason)
    {
        var before = ReadAuthority();
        var document = before.Document;
        var tail = PreparedTail(document, operationId);
        if (!previousProducerAndTransactionTerminated || string.IsNullOrWhiteSpace(freshWitnessId) ||
            !tail.Intent.Input.Matches(observedInput))
            throw new InvalidOperationException("migration_cancellation_input_or_lifetime_unproven");
        var appliedPath = ResolutionPath(operationId, MigrationOperationState.Applied);
        if (File.Exists(Full(appliedPath)))
            throw new InvalidOperationException("migration_applied_receipt_cannot_be_cancelled");
        var resolution = Seal(new MigrationOperationResolution(document.TransactionId, operationId, tail.Intent.Sequence,
            MigrationOperationState.Cancelled, Hash(tail.Intent), observedInput, observedInput, [],
            LastResolution(document), freshWitnessId, false, reason));
        _store.WriteArtifacts(ResolutionArtifacts(before, tail, resolution));
        return Load();
    }

    public MigrationJournalDocument SetStage(MigrationStage stage, MigrationStage? pendingStage,
        string? witnessId = null, string? blockedReason = null)
    {
        var before = ReadAuthority();
        var next = BuildStageTransition(before, stage, pendingStage, witnessId, blockedReason);
        _store.WriteArtifacts(BuildPublication(before, next));
        return next;
    }

    public void Validate(MigrationJournalDocument document)
        => _ = BuildAuthority(document, null, MigrationFileVersion.Hash(Encode(document)));

    private void ValidateCore(MigrationJournalDocument document, IReadOnlyDictionary<string, byte[]>? overlay)
    {
        if (document.SchemaVersion != 2 || !MigrationSwitchTransaction.IsSafeTransactionId(document.TransactionId) || document.Integrity != Hash(document with { Integrity = "" }) ||
            !Enum.IsDefined(document.Stage) || !Enum.IsDefined(document.LastStableStage) ||
            document.PendingStage is { } pending && !Enum.IsDefined(pending) || document.Operations is null)
            throw new InvalidDataException("migration_journal_integrity_or_schema_invalid");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (document.PhaseAuthorization is { } authorization &&
            (!IsHash(authorization.ManifestBytesSha256) ||
             authorization.PrefixCount < 1 || authorization.PrefixCount > document.Operations.Count ||
             authorization.AppliedPrefixDigest != Hash(document.Operations.Take(authorization.PrefixCount).ToArray()) ||
             document.Operations.Take(authorization.PrefixCount).Any(e => e.State != MigrationOperationState.Applied)))
            throw new InvalidDataException("migration_phase_authorization_binding_invalid");
        string? previousResolution = null;
        var appliedByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var versionByPath = new Dictionary<string, MigrationFileVersion>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < document.Operations.Count; index++)
        {
            var entry = document.Operations[index];
            var intent = entry.Intent;
            ValidateIntent(intent);
            if (!IsId(intent.OperationId) || !seen.Add(intent.OperationId) || intent.Sequence != index + 1 ||
                !Enum.IsDefined(intent.Phase) || !Enum.IsDefined(entry.State))
                throw new InvalidDataException("migration_journal_sequence_or_identity_invalid");
            WindowsTxfMigrationVersionStore.Resolve(_root, intent.Path);
            if (intent.PredecessorOperationId != appliedByPath.GetValueOrDefault(intent.Path))
                throw new InvalidDataException("migration_operation_predecessor_invalid");
            if (versionByPath.TryGetValue(intent.Path, out var previousVersion) && !previousVersion.Matches(intent.Input))
                throw new InvalidDataException("migration_operation_input_chain_invalid");
            if (intent.Phase == MigrationOperationPhase.Activate && document.PhaseAuthorization is { } phase &&
                (intent.Sequence != phase.PrefixCount + 1 || !string.Equals(intent.Path, phase.Path, StringComparison.OrdinalIgnoreCase) ||
                 intent.PredecessorOperationId != phase.PredecessorOperationId))
                throw new InvalidDataException("migration_activation_authorization_target_invalid");
            if (entry.State == MigrationOperationState.Prepared)
            {
                if (index != document.Operations.Count - 1 || entry.ResolutionPath is not null || entry.ResolutionSha256 is not null)
                    throw new InvalidDataException("migration_prepared_not_unique_tail");
                var historical = document.Stage is MigrationStage.Blocked or MigrationStage.RollingBack
                    && intent.Phase is MigrationOperationPhase.Reference or MigrationOperationPhase.Activate
                    ? document with { Stage = document.LastStableStage } : document;
                ValidatePreparedPhase(historical, intent.Phase);
                continue;
            }
            var expectedPath = ResolutionPath(intent.OperationId, entry.State);
            if (entry.ResolutionPath != expectedPath || entry.ResolutionSha256 is null)
                throw new InvalidDataException("migration_resolution_binding_invalid");
            var raw = ReadArtifact(expectedPath, overlay);
            if (MigrationFileVersion.Hash(raw) != entry.ResolutionSha256)
                throw new InvalidDataException("migration_resolution_bytes_changed");
            var resolution = JsonSerializer.Deserialize<MigrationOperationResolution>(raw, Options)
                ?? throw new InvalidDataException("migration_resolution_null");
            if (resolution.Integrity != Hash(resolution with { Integrity = "" }) ||
                resolution.TransactionId != document.TransactionId || resolution.OperationId != intent.OperationId ||
                resolution.Sequence != intent.Sequence || resolution.IntentSha256 != Hash(intent) ||
                resolution.State != entry.State || resolution.PreviousResolutionSha256 != previousResolution ||
                !resolution.Input.Matches(intent.Input))
                throw new InvalidDataException("migration_resolution_chain_invalid");
            if (entry.State == MigrationOperationState.Cancelled)
            {
                if (resolution.MutationPerformed || !resolution.Input.Matches(resolution.Output) || string.IsNullOrWhiteSpace(resolution.WitnessId))
                    throw new InvalidDataException("migration_cancelled_claims_mutation");
            }
            else
            {
                if (!resolution.MutationPerformed || (intent.ExpectedOutputSha256 is { } sha && sha != resolution.Output.Sha256))
                    throw new InvalidDataException("migration_applied_data_binding_invalid");
                ValidateOutput(intent, resolution.Output);
                appliedByPath[intent.Path] = intent.OperationId;
                versionByPath[intent.Path] = resolution.Output;
                foreach (var directory in resolution.Directories)
                { appliedByPath[directory.Path] = intent.OperationId; versionByPath[directory.Path] = directory.Output; }
            }
            previousResolution = entry.ResolutionSha256;
        }
        if (document.Stage == MigrationStage.RolledBack && document.Operations.Any(e => e.State == MigrationOperationState.Prepared))
            throw new InvalidDataException("migration_rolled_back_with_prepared_tail");
        if (document.Stage is MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed &&
            document.Operations.Any(e => e.Intent.Phase is MigrationOperationPhase.Undo or MigrationOperationPhase.Restore or MigrationOperationPhase.DeleteAdded or MigrationOperationPhase.RemoveOwnedDirectory))
            throw new InvalidDataException("migration_rollback_operation_in_forward_stage");
    }

    private static void ValidateIntent(MigrationOperationIntent intent)
    {
        ValidateVersion(intent.Input);
        if (intent.Directories is null) throw new InvalidDataException("migration_directory_plan_missing");
        if (intent.ParentInputs is { } parents)
        {
            var normalized = intent.Path.Replace('\\', '/');
            foreach (var parent in parents)
            {
                if (!MigrationSwitchTransaction.IsSafeRelativePath(parent.Key) ||
                    !normalized.StartsWith(parent.Key.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase) ||
                    parent.Value.Kind != MigrationEntryKind.Directory)
                    throw new InvalidDataException("migration_parent_input_not_ancestor");
                ValidateVersion(parent.Value);
            }
            if (parents.Count != normalized.Count(c => c == '/'))
                throw new InvalidDataException("migration_parent_input_incomplete");
            foreach (var directory in intent.Directories)
                if (!parents.TryGetValue(directory.Path, out var input) || input.Exists || !input.Matches(directory.Input))
                    throw new InvalidDataException("migration_created_directory_parent_input_mismatch");
        }
        var deleting = intent.Phase is MigrationOperationPhase.DeleteAdded or MigrationOperationPhase.RemoveOwnedDirectory;
        if (deleting)
        {
            if (!intent.Input.Exists || intent.ExpectedOutputSha256 is not null || intent.OutputBlob is not null)
                throw new InvalidDataException("migration_delete_intent_invalid");
        }
        else if (!IsHash(intent.ExpectedOutputSha256) || string.IsNullOrWhiteSpace(intent.OutputBlob) || intent.Input.Kind != MigrationEntryKind.File)
            throw new InvalidDataException("migration_file_output_intent_invalid");
        if (intent.Phase is MigrationOperationPhase.Activate or MigrationOperationPhase.Undo or MigrationOperationPhase.Restore && !intent.Input.Exists)
            throw new InvalidDataException("migration_transition_without_input");
        if (intent.Phase == MigrationOperationPhase.RemoveOwnedDirectory && intent.Input.Kind != MigrationEntryKind.Directory)
            throw new InvalidDataException("migration_directory_delete_kind_invalid");
    }

    private static void ValidateOutput(MigrationOperationIntent intent, MigrationFileVersion output)
    {
        ValidateVersion(output);
        var deleting = intent.Phase is MigrationOperationPhase.DeleteAdded or MigrationOperationPhase.RemoveOwnedDirectory;
        if (deleting ? output.Exists || output.Kind != intent.Input.Kind :
            !output.Exists || output.Kind != MigrationEntryKind.File || output.Sha256 != intent.ExpectedOutputSha256)
            throw new InvalidDataException("migration_output_kind_or_hash_invalid");
        if (!deleting && intent.Input.Exists && (output.FileId != intent.Input.FileId || output.VolumeSerial != intent.Input.VolumeSerial ||
            !string.Equals(output.VolumeGuid, intent.Input.VolumeGuid, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("migration_modified_output_identity_changed");
    }

    private static void ValidateVersion(MigrationFileVersion version)
    {
        if (!Enum.IsDefined(version.Kind)) throw new InvalidDataException("migration_version_kind_invalid");
        if (!version.Exists)
        {
            if (version.VolumeGuid is not null || version.VolumeSerial is not null || version.FileId is not null ||
                version.LinkCount is not null || version.Length is not null || version.Sha256 is not null)
                throw new InvalidDataException("migration_absence_contains_identity");
        }
        else if (string.IsNullOrWhiteSpace(version.VolumeGuid) || version.VolumeSerial is null || version.FileId is null || version.LinkCount != 1 ||
                 (version.Kind == MigrationEntryKind.File ? version.Length is null or < 0 || !IsHash(version.Sha256) : version.Length is not null || version.Sha256 is not null))
            throw new InvalidDataException("migration_existing_version_invalid");
    }

    private IReadOnlyDictionary<string, MigrationFileVersion>? ExpectedParents(string path)
    {
        var authority = ReadAuthority();
        if (authority.Document.Declaration is null) return null;
        var versions = ReplayDeclaredVersions(authority);
        return Ancestors(path).ToDictionary(p => p, p => versions.GetValueOrDefault(p) ?? MigrationFileVersion.Absent(MigrationEntryKind.Directory), StringComparer.OrdinalIgnoreCase);
    }

    private static string[] Ancestors(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return Enumerable.Range(1, parts.Length - 1).Select(n => string.Join('/', parts.Take(n))).ToArray();
    }

    private static Dictionary<string, MigrationFileVersion> ReplayDeclaredVersions(MigrationAuthorityView view)
    {
        var document = view.Document;
        var declaration = document.Declaration!;
        if (declaration.BaselineEntries is null) throw new InvalidDataException("migration_declared_s0_missing");
        var versions = new Dictionary<string, MigrationFileVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseline in declaration.BaselineEntries)
        {
            if (!MigrationSwitchTransaction.IsSafeRelativePath(baseline.Path) || !baseline.Version.Exists || !versions.TryAdd(baseline.Path, baseline.Version))
                throw new InvalidDataException("migration_declared_s0_invalid");
            ValidateVersion(baseline.Version);
        }
        var s0 = new Dictionary<string, MigrationFileVersion>(versions, StringComparer.OrdinalIgnoreCase);
        var predecessors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ownedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastPhases = new Dictionary<string, MigrationOperationPhase>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Operations)
        {
            var intent = entry.Intent;
            var ancestors = Ancestors(intent.Path);
            if (intent.ParentInputs is null || intent.ParentInputs.Keys.Select(MigrationSwitchTransaction.NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != intent.ParentInputs.Count ||
                !new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase).SetEquals(intent.ParentInputs.Keys))
                throw new InvalidDataException("migration_declared_parent_set_invalid");
            foreach (var parent in ancestors)
            {
                var expected = versions.GetValueOrDefault(parent) ?? MigrationFileVersion.Absent(MigrationEntryKind.Directory);
                if (!expected.Matches(intent.ParentInputs[parent])) throw new InvalidDataException("migration_declared_parent_version_invalid");
            }
            var input = versions.GetValueOrDefault(intent.Path) ?? MigrationFileVersion.Absent(intent.Input.Kind);
            if (!input.Matches(intent.Input) || intent.PredecessorOperationId != predecessors.GetValueOrDefault(intent.Path))
                throw new InvalidDataException("migration_declared_input_or_predecessor_invalid");
            var missing = ancestors.Where(p => !versions.ContainsKey(p)).ToArray();
            if (!new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase).SetEquals(intent.Directories.Select(d => d.Path)) ||
                intent.Directories.Count != missing.Length || missing.Length != 0 && intent.Phase != MigrationOperationPhase.Reference)
                throw new InvalidDataException("migration_declared_directory_plan_invalid");
            foreach (var directory in intent.Directories)
            {
                ValidateVersion(directory.Output);
                if (directory.Input.Exists || directory.Input.Kind != MigrationEntryKind.Directory ||
                    !directory.Output.Exists || directory.Output.Kind != MigrationEntryKind.Directory || s0.ContainsKey(directory.Path))
                    throw new InvalidDataException("migration_declared_directory_ownership_invalid");
            }
            if (intent.Phase == MigrationOperationPhase.Restore && (!s0.TryGetValue(intent.Path, out var baseline) || intent.ExpectedOutputSha256 != baseline.Sha256))
                throw new InvalidDataException("migration_restore_output_not_s0");
            if (intent.Phase == MigrationOperationPhase.RemoveOwnedDirectory && !ownedDirectories.Contains(intent.Path))
                throw new InvalidDataException("migration_directory_delete_without_creation");
            if (entry.State == MigrationOperationState.Prepared) continue;
            var resolution = view.Resolutions[intent.OperationId];
            if (entry.State == MigrationOperationState.Cancelled)
            {
                if (resolution.Directories.Count != 0) throw new InvalidDataException("migration_cancelled_directory_effect");
                continue;
            }
            if (Hash(resolution.Directories) != Hash(intent.Directories)) throw new InvalidDataException("migration_directory_receipt_plan_mismatch");
            if (intent.Phase == MigrationOperationPhase.Restore && !s0[intent.Path].Matches(resolution.Output))
                throw new InvalidDataException("migration_restore_version_not_s0");
            foreach (var directory in resolution.Directories)
            {
                if (!ownedDirectories.Add(directory.Path)) throw new InvalidDataException("migration_directory_creation_reused");
                versions[directory.Path] = directory.Output; predecessors[directory.Path] = intent.OperationId;
            }
            if (resolution.Output.Exists) versions[intent.Path] = resolution.Output; else versions.Remove(intent.Path);
            if (intent.Phase == MigrationOperationPhase.RemoveOwnedDirectory) ownedDirectories.Remove(intent.Path);
            predecessors[intent.Path] = intent.OperationId; lastPhases[intent.Path] = intent.Phase;
        }
        foreach (var observation in document.BaselineObservations ?? [])
        {
            if (observation.MutationPerformed || observation.Reason != "already_exact_baseline_no_write" ||
                !s0.TryGetValue(observation.Path, out var baseline) || !baseline.Matches(observation.Observed) || baseline.Sha256 != observation.BaselineHash ||
                observation.LatestAppliedOperationId != predecessors.GetValueOrDefault(observation.Path))
                throw new InvalidDataException("migration_declared_baseline_observation_invalid");
            versions[observation.Path] = observation.Observed;
        }
        if (document.Stage == MigrationStage.RolledBack || document.Stage == MigrationStage.RollingBack && document.PendingStage == MigrationStage.RolledBack)
        {
            if (versions.Count != s0.Count || s0.Any(p => !versions.TryGetValue(p.Key, out var actual) || !p.Value.Matches(actual)) || ownedDirectories.Count != 0)
                throw new InvalidDataException("migration_rolledback_root_not_s0");
            foreach (var target in declaration.References)
                if (lastPhases.TryGetValue(target.Target.Path, out var phase) &&
                    (target.Target.Kind == ChangeKind.Added ? phase != MigrationOperationPhase.DeleteAdded : phase != MigrationOperationPhase.Restore &&
                        !(document.BaselineObservations ?? []).Any(o => string.Equals(o.Path, target.Target.Path, StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("migration_rolledback_progress_incomplete");
        }
        return versions;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static void ValidatePreparedPhase(MigrationJournalDocument document, MigrationOperationPhase phase)
    {
        var stage = document.Stage;
        var allowed = phase switch
        {
            MigrationOperationPhase.Reference => stage == MigrationStage.SnapshotReady && document.PendingStage is null,
            MigrationOperationPhase.Activate => document.Declaration is not null
                ? stage == MigrationStage.ReferenceUpdating && document.PendingStage == MigrationStage.Activated && document.PhaseAuthorization is not null
                : stage == MigrationStage.ReferenceUpdating ||
                stage == MigrationStage.SnapshotReady && document.PendingStage == MigrationStage.Activated && document.PhaseAuthorization is not null,
            _ => stage is MigrationStage.RollingBack or MigrationStage.Blocked
        };
        if (!allowed) throw new InvalidDataException("migration_operation_phase_not_allowed_in_stage");
    }

    private static bool CanTransition(MigrationStage from, MigrationStage to) => from == to || to == MigrationStage.Blocked ||
        (to == MigrationStage.RollingBack && from is MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed or MigrationStage.Blocked) ||
        (from, to) is (MigrationStage.Snapshotting, MigrationStage.SnapshotReady) or (MigrationStage.SnapshotReady, MigrationStage.ReferenceUpdating) or
        (MigrationStage.ReferenceUpdating, MigrationStage.Activated) or (MigrationStage.Activated, MigrationStage.Committed) or
        (MigrationStage.RollingBack, MigrationStage.RolledBack);

    private IReadOnlyList<MigrationArtifactWrite> ResolutionArtifacts(MigrationAuthorityView before,
        MigrationJournalEntry tail, MigrationOperationResolution resolution)
    {
        var path = ResolutionPath(tail.Intent.OperationId, resolution.State);
        var bytes = Encode(resolution);
        var entries = before.Document.Operations.ToArray();
        entries[^1] = tail with { State = resolution.State, ResolutionPath = path,
            ResolutionSha256 = MigrationFileVersion.Hash(bytes) };
        var next = before.Document with { Operations = entries };
        if (resolution.State == MigrationOperationState.Cancelled && next.Declaration is not null)
            next = next with { Stage = MigrationStage.RollingBack, PendingStage = null };
        return BuildPublication(before, next, [new(path, bytes)]);
    }

    private void Publish(MigrationJournalDocument next, MigrationJournalDocument previous)
    {
        var before = ReadAuthority();
        if (Hash(before.Document) != Hash(previous)) throw new InvalidDataException("migration_publication_input_changed");
        _store.WriteArtifacts(BuildPublication(before, next));
    }

    public MigrationAuthorityView ReadAuthority()
    {
        var raw = File.ReadAllBytes(Full(DocumentPath));
        var document = JsonSerializer.Deserialize<MigrationJournalDocument>(raw, Options)
            ?? throw new InvalidDataException("migration_journal_null");
        return BuildAuthority(document, null, MigrationFileVersion.Hash(raw));
    }

    private byte[] ReadArtifact(string relative, IReadOnlyDictionary<string, byte[]>? overlay = null)
    {
        _ = Full(relative);
        return overlay is not null && overlay.TryGetValue(relative, out var bytes)
            ? bytes : File.ReadAllBytes(Full(relative));
    }

    private string StorePath(string relative)
    {
        _ = Full(relative);
        return _artifactPrefix.Length == 0 ? relative : _artifactPrefix + "/" + relative.Replace('\\', '/');
    }

    private MigrationAuthorityView BuildAuthority(MigrationJournalDocument document,
        IReadOnlyDictionary<string, byte[]>? overlay, string documentBytesHash)
    {
        ValidateCore(document, overlay);
        var resolutions = new Dictionary<string, MigrationOperationResolution>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Operations)
        {
            if (entry.Intent.OutputBlob is { } blob)
            {
                if (!paths.Add(blob)) throw new InvalidDataException("migration_evidence_path_reused");
                var bytes = ReadArtifact(blob, overlay);
                if (MigrationFileVersion.Hash(bytes) != entry.Intent.ExpectedOutputSha256)
                    throw new InvalidDataException("migration_output_blob_integrity_invalid");
                outputs.Add(entry.Intent.OperationId, bytes);
            }
            if (entry.State == MigrationOperationState.Prepared) continue;
            if (!paths.Add(entry.ResolutionPath!)) throw new InvalidDataException("migration_evidence_path_reused");
            var resolution = JsonSerializer.Deserialize<MigrationOperationResolution>(ReadArtifact(entry.ResolutionPath!, overlay), Options)
                ?? throw new InvalidDataException("migration_resolution_null");
            resolutions.Add(entry.Intent.OperationId, resolution);
        }
        var resolved = document.Operations.Where(e => e.State != MigrationOperationState.Prepared).ToArray();
        var applied = document.Operations.Where(e => e.State == MigrationOperationState.Applied).ToArray();
        var view = new MigrationAuthorityView(document, documentBytesHash, resolutions, outputs, Hash(resolved), Hash(applied));
        ValidateDeclarationAndStages(view);
        if (document.Declaration is not null) ReplayDeclaredVersions(view);
        return view;
    }

    internal IReadOnlyList<MigrationArtifactWrite> BuildPublication(MigrationAuthorityView? previous,
        MigrationJournalDocument candidate, IReadOnlyList<MigrationArtifactWrite>? relativeExtras = null,
        IReadOnlyList<MigrationArtifactWrite>? rootExtras = null,
        Func<MigrationAuthorityView?, MigrationAuthorityView, IReadOnlyList<MigrationArtifactWrite>>? companionOverride = null)
    {
        candidate = Seal(candidate);
        var bytes = Encode(candidate);
        var overlay = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var write in relativeExtras ?? [])
        {
            _ = Full(write.Path);
            if (!overlay.TryAdd(write.Path, write.Bytes)) throw new InvalidDataException("migration_duplicate_candidate_artifact");
        }
        var next = BuildAuthority(candidate, overlay, MigrationFileVersion.Hash(bytes));
        var writes = new List<MigrationArtifactWrite>();
        foreach (var write in relativeExtras ?? []) writes.Add(write with { Path = StorePath(write.Path) });
        writes.Add(new(StorePath(DocumentPath), bytes, previous?.JournalBytesSha256));
        var companion = companionOverride ?? _companion;
        if (companion is not null) writes.AddRange(companion(previous, next));
        if (rootExtras is not null) writes.AddRange(rootExtras);
        if (writes.Select(w => w.Path.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != writes.Count)
            throw new InvalidDataException("migration_publication_path_collision");
        return writes;
    }

    public MigrationJournalDocument FreezeDeclaration(MigrationFrozenDeclaration declaration)
    {
        var before = ReadAuthority();
        var document = before.Document;
        if (document.Declaration is { } old && Hash(old) == Hash(declaration)) return document;
        if (document.RegistrationFrozen || document.Operations.Count != 0)
            throw new InvalidOperationException("migration_declaration_changed_after_prepared");
        if (document.Stage != MigrationStage.SnapshotReady)
            throw new InvalidOperationException("migration_declaration_stage_invalid");
        var next = document with { Declaration = declaration };
        _store.WriteArtifacts(BuildPublication(before, next));
        return ReadAuthority().Document;
    }

    internal MigrationJournalDocument BuildStageTransition(MigrationAuthorityView before, MigrationStage stage,
        MigrationStage? pendingStage, string? witnessId = null, string? blockedReason = null)
    {
        var document = before.Document;
        if (!CanTransition(document.Stage, stage)) throw new InvalidOperationException("migration_invalid_stage_transition");
        if (stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed or MigrationStage.RolledBack)
        {
            if (string.IsNullOrWhiteSpace(witnessId)) throw new InvalidOperationException("migration_success_requires_fresh_witness");
            if (document.Operations.Any(e => e.State == MigrationOperationState.Prepared))
                throw new InvalidOperationException("migration_success_with_prepared_tail");
            if (stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed)
            {
                if (!document.Operations.Any(e => e.State == MigrationOperationState.Applied && e.Intent.Phase == MigrationOperationPhase.Reference))
                    throw new InvalidOperationException("migration_reference_success_without_applied_data");
                if ((stage is MigrationStage.Activated or MigrationStage.Committed) &&
                    !document.Operations.Any(e => e.State == MigrationOperationState.Applied && e.Intent.Phase == MigrationOperationPhase.Activate))
                    throw new InvalidOperationException("migration_activation_success_without_applied_data");
            }
        }
        if (stage == MigrationStage.Blocked && string.IsNullOrWhiteSpace(blockedReason))
            throw new InvalidOperationException("migration_blocked_reason_required");
        var stable = stage is MigrationStage.Blocked or MigrationStage.RollingBack ? document.LastStableStage : stage;
        // PhaseAuthorization is historical evidence. Retain its pending binding until that phase is resolved.
        var retainPhase = document.PhaseAuthorization is not null && document.Operations.Any(e => e.State == MigrationOperationState.Prepared);
        var next = Seal(document with { Stage = stage, LastStableStage = stable,
            PendingStage = retainPhase ? document.PendingStage : pendingStage, StageWitnessId = witnessId,
            BlockedReason = blockedReason, PhaseAuthorization = document.PhaseAuthorization });
        _ = BuildAuthority(next, null, MigrationFileVersion.Hash(Encode(next)));
        return next;
    }

    private static void ValidateDeclarationAndStages(MigrationAuthorityView view)
    {
        var document = view.Document;
        if (document.Declaration is not { } declaration) return; // Old component/compatibility format is not new forward authority.
        if (declaration.TransactionId != document.TransactionId || string.IsNullOrWhiteSpace(declaration.SnapshotId) ||
            !IsHash(declaration.SnapshotManifestHash) || declaration.References is null || declaration.ChangedFiles is null)
            throw new InvalidDataException("migration_declaration_invalid");
        if (document.Stage is MigrationStage.None or MigrationStage.Snapshotting ||
            document.LastStableStage is MigrationStage.None or MigrationStage.Snapshotting ||
            document.Operations.Count != 0 && !document.RegistrationFrozen)
            throw new InvalidDataException("migration_declared_stage_or_freeze_invalid");
        var pendingAllowed = document.Stage switch
        {
            MigrationStage.SnapshotReady => document.PendingStage is null or MigrationStage.ReferenceUpdating,
            MigrationStage.ReferenceUpdating => document.PendingStage is null || document.PendingStage == MigrationStage.Activated && document.PhaseAuthorization is not null,
            MigrationStage.Activated => document.PendingStage is null or MigrationStage.Committed,
            MigrationStage.Committed or MigrationStage.RolledBack => document.PendingStage is null,
            MigrationStage.RollingBack => document.PendingStage is null or MigrationStage.RolledBack ||
                document.Operations.Any(e => e.State == MigrationOperationState.Prepared),
            MigrationStage.Blocked => document.PendingStage is null || (document.LastStableStage, document.PendingStage) switch
            {
                (MigrationStage.SnapshotReady, MigrationStage.ReferenceUpdating) => true,
                (MigrationStage.ReferenceUpdating, MigrationStage.Activated) => document.PhaseAuthorization is not null,
                (MigrationStage.Activated, MigrationStage.Committed) => document.Operations.Any(e => e.State == MigrationOperationState.Applied && e.Intent.Phase == MigrationOperationPhase.Activate),
                _ => false
            },
            _ => false
        };
        if (!pendingAllowed || document.Stage == MigrationStage.SnapshotReady && document.Operations.Any(e => e.Intent.Phase != MigrationOperationPhase.Reference))
            throw new InvalidDataException("migration_declared_pending_or_phase_invalid");
        if (document.Stage == MigrationStage.ReferenceUpdating && document.Operations.Any(e => e.Intent.Phase == MigrationOperationPhase.Activate) &&
            (document.PendingStage != MigrationStage.Activated || document.PhaseAuthorization is null))
            throw new InvalidDataException("migration_activation_candidate_binding_missing");
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in declaration.References)
        {
            if (item.Target is null || item.Target.Kind is not (ChangeKind.Added or ChangeKind.Modified) ||
                !MigrationSwitchTransaction.IsSafeRelativePath(item.Target.Path) || !declared.Add(item.Target.Path) || !IsHash(item.OutputSha256))
                throw new InvalidDataException("migration_declaration_target_invalid");
            ValidateVersion(item.Input);
            if (item.Target.Kind == ChangeKind.Added ? item.Input.Exists : !item.Input.Exists)
                throw new InvalidDataException("migration_declaration_input_kind_invalid");
        }
        if (declaration.ChangedFiles.Count != declaration.ChangedFiles.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() ||
            declaration.ChangedFiles.Any(c => !MigrationSwitchTransaction.IsSafeRelativePath(c.Path) || !Enum.IsDefined(c.Kind)) ||
            !declared.SetEquals(declaration.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted).Select(c => c.Path)))
            throw new InvalidDataException("migration_declaration_registry_mismatch");
        var references = document.Operations.Where(e => e.Intent.Phase == MigrationOperationPhase.Reference).ToArray();
        if (references.Length > declaration.References.Count) throw new InvalidDataException("migration_reference_exceeds_declaration");
        for (var index = 0; index < references.Length; index++)
        {
            var entry = references[index]; var item = declaration.References[index];
            if (!string.Equals(entry.Intent.Path, item.Target.Path, StringComparison.OrdinalIgnoreCase) ||
                !entry.Intent.Input.Matches(item.Input) || entry.Intent.ExpectedOutputSha256 != item.OutputSha256)
                throw new InvalidDataException("migration_reference_differs_from_frozen_declaration");
        }
        var appliedByPath = new Dictionary<string, MigrationOperationPhase>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Operations.Where(e => e.State == MigrationOperationState.Applied))
        {
            var phase = entry.Intent.Phase;
            if (phase == MigrationOperationPhase.RemoveOwnedDirectory) continue;
            var hasPrevious = appliedByPath.TryGetValue(entry.Intent.Path, out var previous);
            var valid = phase switch
            {
                MigrationOperationPhase.Reference => !hasPrevious,
                MigrationOperationPhase.Activate => hasPrevious && previous == MigrationOperationPhase.Reference,
                MigrationOperationPhase.Undo => hasPrevious && previous == MigrationOperationPhase.Activate,
                MigrationOperationPhase.Restore or MigrationOperationPhase.DeleteAdded => hasPrevious &&
                    (previous is MigrationOperationPhase.Reference or MigrationOperationPhase.Undo) &&
                    declaration.References.Any(r => string.Equals(r.Target.Path, entry.Intent.Path, StringComparison.OrdinalIgnoreCase) &&
                        (phase == MigrationOperationPhase.DeleteAdded ? r.Target.Kind == ChangeKind.Added : r.Target.Kind == ChangeKind.Modified)),
                _ => false
            };
            if (!valid) throw new InvalidDataException("migration_actual_data_edge_invalid");
            appliedByPath[entry.Intent.Path] = phase;
        }
        var forward = document.Stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed;
        if (forward && (references.Length != declaration.References.Count || references.Any(e => e.State != MigrationOperationState.Applied) ||
            document.Operations.Any(e => e.State == MigrationOperationState.Cancelled ||
                e.State == MigrationOperationState.Prepared && !(document.Stage == MigrationStage.ReferenceUpdating &&
                    document.PendingStage == MigrationStage.Activated && e.Intent.Phase == MigrationOperationPhase.Activate) || e.Intent.Phase is
                MigrationOperationPhase.Undo or MigrationOperationPhase.Restore or MigrationOperationPhase.DeleteAdded or MigrationOperationPhase.RemoveOwnedDirectory)))
            throw new InvalidDataException("migration_success_without_complete_frozen_reference");
        if (document.Stage is MigrationStage.Activated or MigrationStage.Committed &&
            document.Operations.Count(e => e.State == MigrationOperationState.Applied && e.Intent.Phase == MigrationOperationPhase.Activate) != 1)
            throw new InvalidDataException("migration_success_without_unique_activation");
        if (forward || document.Stage == MigrationStage.RolledBack)
            if (string.IsNullOrWhiteSpace(document.StageWitnessId) || document.Stage != MigrationStage.ReferenceUpdating &&
                document.Operations.Any(e => e.State == MigrationOperationState.Prepared))
                throw new InvalidDataException("migration_success_without_stage_witness");
        if (document.Stage == MigrationStage.Blocked && string.IsNullOrWhiteSpace(document.BlockedReason))
            throw new InvalidDataException("migration_blocked_without_reason");
    }


    private string Full(string relative) => WindowsTxfMigrationVersionStore.Resolve(_root, relative);
    private static string? LastResolution(MigrationJournalDocument document)
        => document.Operations.LastOrDefault(e => e.State != MigrationOperationState.Prepared)?.ResolutionSha256;
    private static MigrationJournalEntry PreparedTail(MigrationJournalDocument document, string operationId)
    {
        var tail = document.Operations.LastOrDefault();
        if (tail is null || tail.State != MigrationOperationState.Prepared || tail.Intent.OperationId != operationId)
            throw new InvalidOperationException("migration_operation_not_prepared_tail");
        return tail;
    }
    private static bool IsId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);
    private static string ResolutionPath(string id, MigrationOperationState state) => "resolutions/" + id + "." + state.ToString().ToLowerInvariant() + ".json";
    private static MigrationJournalDocument Seal(MigrationJournalDocument value) => value with { Integrity = Hash(value with { Integrity = "" }) };
    private static MigrationOperationResolution Seal(MigrationOperationResolution value) => value with { Integrity = Hash(value with { Integrity = "" }) };
    public static string Hash<T>(T value) => MigrationFileVersion.Hash(Encode(value));
    public static byte[] Encode<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element);
        return stream.ToArray();
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else element.WriteTo(writer);
    }
}
