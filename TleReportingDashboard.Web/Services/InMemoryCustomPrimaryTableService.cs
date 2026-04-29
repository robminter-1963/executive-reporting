using System.Collections.Concurrent;

namespace TleReportingDashboard.Web.Services;

// Mock used in dev-without-DB mode. Keeps entries in a per-process dictionary
// so the picker behaves the same as the live service within a session; state
// disappears on restart, which is fine for dev.
public sealed class InMemoryCustomPrimaryTableService : ICustomPrimaryTableService
{
    private readonly ConcurrentDictionary<Guid, CustomPrimaryTableRecord> _rows = new();
    // Per-primary role → owner-field map. Keyed by primary table id.
    private readonly ConcurrentDictionary<Guid, Dictionary<Guid, string>> _roleOwners = new();

    public Task<List<CustomPrimaryTableRecord>> GetByConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        var result = _rows.Values
            .Where(r => r.ConnectionId == connectionId)
            .OrderByDescending(r => r.IsDefaultPrimary)
            .ThenByDescending(r => r.IsPrimary)
            .ThenBy(r => r.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Alias, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<CustomPrimaryTableRecord> AddAsync(
        Guid connectionId, string tableName, string? alias,
        bool isPrimary, bool isDefaultPrimary,
        string? createdById, string? createdByEmail,
        CancellationToken ct = default,
        string? tableType = null,
        string? primaryColumn = null,
        string? additionalKeyColumns = null)
    {
        if (!PrimaryTableRef.TableRegex().IsMatch(tableName))
            throw new ArgumentException("Table name contains invalid characters.", nameof(tableName));
        var normalizedAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias.Trim();
        if (normalizedAlias.Length > 0 && !PrimaryTableRef.AliasRegex().IsMatch(normalizedAlias))
            throw new ArgumentException("Alias must start with a letter/underscore and contain only letters, digits, or underscores.", nameof(alias));
        alias = normalizedAlias;

        // Same normalization rules as the SQL service so behavior matches
        // in dev-without-DB mode.
        var normalizedTableType = string.IsNullOrWhiteSpace(tableType) ? null : tableType.Trim();
        var normalizedPrimaryColumn = NormalizeIdentifier(primaryColumn, "primaryColumn");
        var normalizedAdditionalKeys = NormalizeKeyColumnList(additionalKeyColumns);

        if (isDefaultPrimary) isPrimary = true;

        var existing = _rows.Values.FirstOrDefault(r =>
            r.ConnectionId == connectionId
            && string.Equals(r.TableName, tableName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // Idempotent: refresh flags and return the same row.
            if (isDefaultPrimary) ClearDefaultsOnConnection(connectionId, existing.Id);
            existing.IsPrimary = isPrimary;
            existing.IsDefaultPrimary = isDefaultPrimary;
            existing.TableType = normalizedTableType;
            existing.PrimaryColumn = normalizedPrimaryColumn;
            existing.AdditionalKeyColumns = normalizedAdditionalKeys;
            return Task.FromResult(existing);
        }

        if (normalizedAlias.Length > 0)
        {
            var clash = _rows.Values.FirstOrDefault(r =>
                r.ConnectionId == connectionId
                && string.Equals(r.Alias, normalizedAlias, StringComparison.OrdinalIgnoreCase));
            if (clash is not null)
            {
                throw new InvalidOperationException(
                    $"Alias \"{normalizedAlias}\" is already used by {clash.TableName} on this connection. Aliases must be unique per connection.");
            }
        }

        if (isDefaultPrimary) ClearDefaultsOnConnection(connectionId, excludeId: null);

        var record = new CustomPrimaryTableRecord
        {
            Id = Guid.NewGuid(),
            ConnectionId = connectionId,
            TableName = tableName,
            Alias = alias,
            IsPrimary = isPrimary,
            IsDefaultPrimary = isDefaultPrimary,
            CreatedAt = DateTime.UtcNow,
            CreatedById = createdById,
            CreatedByEmail = createdByEmail,
            TableType = normalizedTableType,
            PrimaryColumn = normalizedPrimaryColumn,
            AdditionalKeyColumns = normalizedAdditionalKeys
        };
        _rows[record.Id] = record;
        return Task.FromResult(record);
    }

    public Task UpdateAsync(
        Guid id, string tableName, string? alias,
        bool isPrimary, bool isDefaultPrimary,
        CancellationToken ct = default,
        string? tableType = null,
        string? primaryColumn = null,
        string? additionalKeyColumns = null)
    {
        if (!PrimaryTableRef.TableRegex().IsMatch(tableName))
            throw new ArgumentException("Table name contains invalid characters.", nameof(tableName));
        var normalizedAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias.Trim();
        if (normalizedAlias.Length > 0 && !PrimaryTableRef.AliasRegex().IsMatch(normalizedAlias))
            throw new ArgumentException("Alias must start with a letter/underscore and contain only letters, digits, or underscores.", nameof(alias));

        var normalizedTableType = string.IsNullOrWhiteSpace(tableType) ? null : tableType.Trim();
        var normalizedPrimaryColumn = NormalizeIdentifier(primaryColumn, "primaryColumn");
        var normalizedAdditionalKeys = NormalizeKeyColumnList(additionalKeyColumns);

        if (isDefaultPrimary) isPrimary = true;

        if (_rows.TryGetValue(id, out var existing))
        {
            if (normalizedAlias.Length > 0)
            {
                var clash = _rows.Values.FirstOrDefault(r =>
                    r.Id != id
                    && r.ConnectionId == existing.ConnectionId
                    && string.Equals(r.Alias, normalizedAlias, StringComparison.OrdinalIgnoreCase));
                if (clash is not null)
                {
                    throw new InvalidOperationException(
                        $"Alias \"{normalizedAlias}\" is already used by {clash.TableName} on this connection. Aliases must be unique per connection.");
                }
            }

            if (isDefaultPrimary) ClearDefaultsOnConnection(existing.ConnectionId, excludeId: id);

            existing.TableName = tableName;
            existing.Alias = normalizedAlias;
            existing.IsPrimary = isPrimary;
            existing.IsDefaultPrimary = isDefaultPrimary;
            existing.TableType = normalizedTableType;
            existing.PrimaryColumn = normalizedPrimaryColumn;
            existing.AdditionalKeyColumns = normalizedAdditionalKeys;
        }
        return Task.CompletedTask;
    }

    // Same identifier rules as CustomPrimaryTableService — kept inline so
    // dev-without-DB mode validates the same way the live service does.
    private static string? NormalizeIdentifier(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (!PrimaryTableRef.AliasRegex().IsMatch(trimmed))
            throw new ArgumentException(
                $"\"{trimmed}\" is not a valid column name. Use letters, digits, and underscores only.",
                paramName);
        return trimmed;
    }

    private static string? NormalizeKeyColumnList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Count == 0) return null;
        foreach (var p in parts)
        {
            if (!PrimaryTableRef.AliasRegex().IsMatch(p))
                throw new ArgumentException(
                    $"\"{p}\" is not a valid column name. Use letters, digits, and underscores only.",
                    nameof(value));
        }
        return string.Join(", ", parts);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _rows.TryRemove(id, out _);
        _roleOwners.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    // ── Role-scoped owner fields (in-memory) ──────────────────────────────

    public Task<IReadOnlyDictionary<Guid, string>> GetRoleOwnerFieldsAsync(Guid primaryTableId, CancellationToken ct = default)
    {
        // Return a copy so callers can't mutate our internal map.
        var map = _roleOwners.TryGetValue(primaryTableId, out var existing)
            ? new Dictionary<Guid, string>(existing)
            : new Dictionary<Guid, string>();
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(map);
    }

    public Task SetRoleOwnerAsync(Guid primaryTableId, Guid roleId, string ownerFieldId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerFieldId))
            throw new ArgumentException("ownerFieldId is required — use ClearRoleOwnerAsync to remove a mapping.", nameof(ownerFieldId));

        var map = _roleOwners.GetOrAdd(primaryTableId, _ => new Dictionary<Guid, string>());
        lock (map)
        {
            map[roleId] = ownerFieldId.Trim();
        }
        return Task.CompletedTask;
    }

    public Task ClearRoleOwnerAsync(Guid primaryTableId, Guid roleId, CancellationToken ct = default)
    {
        if (_roleOwners.TryGetValue(primaryTableId, out var map))
        {
            lock (map) { map.Remove(roleId); }
        }
        return Task.CompletedTask;
    }

    public Task<string?> ResolveOwnerFieldForRoleAsync(Guid primaryTableId, Guid roleId, CancellationToken ct = default)
    {
        if (_roleOwners.TryGetValue(primaryTableId, out var map))
        {
            lock (map)
            {
                return Task.FromResult(map.TryGetValue(roleId, out var v) ? v : null);
            }
        }
        return Task.FromResult<string?>(null);
    }

    // Mirror of the filtered unique index on the DB side — clear every
    // default-flagged row on the connection except the one we're about to
    // set (or all of them, when excludeId is null).
    private void ClearDefaultsOnConnection(Guid connectionId, Guid? excludeId)
    {
        foreach (var r in _rows.Values)
        {
            if (r.ConnectionId != connectionId) continue;
            if (excludeId is Guid ex && r.Id == ex) continue;
            r.IsDefaultPrimary = false;
        }
    }
}
