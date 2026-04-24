using System.Collections.Concurrent;

namespace TleReportingDashboard.Web.Services;

// Mock used in dev-without-DB mode. Keeps entries in a per-process dictionary
// so the picker behaves the same as the live service within a session; state
// disappears on restart, which is fine for dev.
public sealed class InMemoryCustomPrimaryTableService : ICustomPrimaryTableService
{
    private readonly ConcurrentDictionary<Guid, CustomPrimaryTableRecord> _rows = new();

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
        CancellationToken ct = default)
    {
        if (!PrimaryTableRef.TableRegex().IsMatch(tableName))
            throw new ArgumentException("Table name contains invalid characters.", nameof(tableName));
        var normalizedAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias.Trim();
        if (normalizedAlias.Length > 0 && !PrimaryTableRef.AliasRegex().IsMatch(normalizedAlias))
            throw new ArgumentException("Alias must start with a letter/underscore and contain only letters, digits, or underscores.", nameof(alias));
        alias = normalizedAlias;

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
            CreatedByEmail = createdByEmail
        };
        _rows[record.Id] = record;
        return Task.FromResult(record);
    }

    public Task UpdateAsync(
        Guid id, string tableName, string? alias,
        bool isPrimary, bool isDefaultPrimary,
        CancellationToken ct = default)
    {
        if (!PrimaryTableRef.TableRegex().IsMatch(tableName))
            throw new ArgumentException("Table name contains invalid characters.", nameof(tableName));
        var normalizedAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias.Trim();
        if (normalizedAlias.Length > 0 && !PrimaryTableRef.AliasRegex().IsMatch(normalizedAlias))
            throw new ArgumentException("Alias must start with a letter/underscore and contain only letters, digits, or underscores.", nameof(alias));

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
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _rows.TryRemove(id, out _);
        return Task.CompletedTask;
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
