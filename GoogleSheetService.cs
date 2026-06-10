using ClanGuardBot.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

// RosterRow is defined in RosterExportService.cs

/// <summary>
/// Result of a gamertag lookup. All fields are non-null but may be empty strings.
/// </summary>
public record GamertagLookupResult(
    string DiscordName,
    string EA,
    string Steam,
    string PSN,
    string Xbox,
    string Embark,
    string Bungie);

/// <summary>
/// Outcome of a roster reconcile sweep.
///   • <see cref="Deleted"/>       — rows removed (departed members, by ID).
///   • <see cref="Kept"/>          — ID rows whose member is still in the guild.
///   • <see cref="LegacySkipped"/> — rows with no parseable Discord ID; never
///                                   auto-deleted (can't safely confirm departure).
///   • <see cref="Aborted"/>       — a safety brake tripped (too many candidates,
///                                   or the sheet couldn't be resolved); nothing
///                                   was deleted this sweep.
///   • <see cref="Candidates"/>    — how many rows looked departed (deleted only
///                                   when not aborted).
/// </summary>
public record GamertagReconcileResult(int Deleted, int Kept, int LegacySkipped, int Candidates, bool Aborted);

/// <summary>
/// A roster row that has no parseable Discord ID in column A (a legacy / manually
/// added row). <see cref="RowNumber"/> is the 1-based sheet row. ColA/ColB are
/// the raw first two cells so a backfill caller can match by name and an operator
/// can eyeball the layout before anything is written.
/// </summary>
public record LegacyGamertagRow(int RowNumber, string ColA, string ColB);

/// <summary>
/// Writes gamertag data to a Google Sheet.
/// Expects a service account credentials JSON file.
/// </summary>
public class GoogleSheetsService
{
    private readonly BotConfig _config;
    private readonly ILogger<GoogleSheetsService> _logger;

    public GoogleSheetsService(IOptions<BotConfig> config, ILogger<GoogleSheetsService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Appends (or updates) a row in the configured Google Sheet.
    /// Columns: Discord ID | Discord Name | EA | Steam | PSN | Xbox | Embark | Bungie
    /// Matches rows by Discord ID so name changes don't create duplicates.
    /// </summary>
    public async Task WriteGamertagsAsync(
        ulong discordId, string discordName, string ea, string steam, string psn,
        string xbox, string embark, string bungie)
    {
        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        var spreadsheetId = _config.GoogleSpreadsheetId;
        var sheetName = _config.GoogleSheetName;
        var range = $"{sheetName}!A:H";

        // Try to find an existing row for this Discord user by ID
        var getRequest = service.Spreadsheets.Values.Get(spreadsheetId, range);
        var getResponse = await getRequest.ExecuteAsync();

        var newRow = new List<object> { discordId.ToString(), discordName, ea, steam, psn, xbox, embark, bungie };

        if (getResponse.Values is not null)
        {
            // First pass: try to match by Discord ID in column A
            for (int i = 0; i < getResponse.Values.Count; i++)
            {
                var row = getResponse.Values[i];
                if (row.Count > 0 && string.Equals(row[0]?.ToString(), discordId.ToString(), StringComparison.Ordinal))
                {
                    var updateRange = $"{sheetName}!A{i + 1}:H{i + 1}";
                    var updateBody = new ValueRange { Values = new List<IList<object>> { newRow } };
                    var updateRequest = service.Spreadsheets.Values.Update(updateBody, spreadsheetId, updateRange);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest
                        .ValueInputOptionEnum.USERENTERED;
                    await updateRequest.ExecuteAsync();

                    _logger.LogInformation("Updated gamertags for {DiscordName} ({DiscordId}) in row {Row} (matched by ID)",
                        discordName, discordId, i + 1);
                    await SortSheetBySecondColumnAsync(service, spreadsheetId, sheetName);
                    return;
                }
            }

            // Second pass: fall back to matching by Discord Name in column B (legacy rows without ID)
            // Also check column A for legacy rows where name was in column A before the ID column was added
            for (int i = 0; i < getResponse.Values.Count; i++)
            {
                var row = getResponse.Values[i];
                var colA = row.Count > 0 ? row[0]?.ToString() ?? "" : "";
                var colB = row.Count > 1 ? row[1]?.ToString() ?? "" : "";

                if (string.Equals(colB, discordName, StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(colA, discordName, StringComparison.OrdinalIgnoreCase) && !ulong.TryParse(colA, out _)))
                {
                    // Update the row and backfill the Discord ID
                    var updateRange = $"{sheetName}!A{i + 1}:H{i + 1}";
                    var updateBody = new ValueRange { Values = new List<IList<object>> { newRow } };
                    var updateRequest = service.Spreadsheets.Values.Update(updateBody, spreadsheetId, updateRange);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest
                        .ValueInputOptionEnum.USERENTERED;
                    await updateRequest.ExecuteAsync();

                    _logger.LogInformation(
                        "Updated gamertags for {DiscordName} ({DiscordId}) in row {Row} (matched by name, backfilled ID)",
                        discordName, discordId, i + 1);
                    await SortSheetBySecondColumnAsync(service, spreadsheetId, sheetName);
                    return;
                }
            }
        }

        // Append new row
        var appendBody = new ValueRange { Values = new List<IList<object>> { newRow } };
        var appendRequest = service.Spreadsheets.Values.Append(appendBody, spreadsheetId, range);
        appendRequest.ValueInputOption =
            SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
        appendRequest.InsertDataOption =
            SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
        await appendRequest.ExecuteAsync();

        _logger.LogInformation("Appended gamertags for {DiscordName} ({DiscordId})", discordName, discordId);

        await SortSheetBySecondColumnAsync(service, spreadsheetId, sheetName);
    }

    /// <summary>
    /// Removes a member's row from the roster sheet, matched by Discord ID in
    /// column A, and physically deletes the row so no blank gap is left behind.
    /// Called when a member leaves / is kicked / is banned. Returns true if a row
    /// was removed.
    ///
    /// Matches by Discord ID ONLY — legacy rows with no stored ID (name in
    /// column A or B) are intentionally left alone, because matching a departure
    /// by display name risks deleting the wrong person when names collide or
    /// change.
    /// </summary>
    public async Task<bool> DeleteGamertagsAsync(ulong discordId)
    {
        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        var spreadsheetId = _config.GoogleSpreadsheetId;
        var sheetName     = _config.GoogleSheetName;
        var range         = $"{sheetName}!A:H";

        var getResponse = await service.Spreadsheets.Values.Get(spreadsheetId, range).ExecuteAsync();
        if (getResponse.Values is null) return false;

        var idStr    = discordId.ToString();
        var rowIndex = -1; // 0-based index into the sheet (row 0 is the header)
        for (var i = 0; i < getResponse.Values.Count; i++)
        {
            var row = getResponse.Values[i];
            if (row.Count > 0 && string.Equals(row[0]?.ToString(), idStr, StringComparison.Ordinal))
            {
                rowIndex = i;
                break;
            }
        }

        if (rowIndex < 0)
        {
            _logger.LogDebug("No gamertag row to delete for {DiscordId}", discordId);
            return false;
        }

        // DeleteDimension needs the numeric sheet ID, not its name.
        var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
        var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == sheetName);
        if (sheet is null)
        {
            _logger.LogWarning("Sheet {SheetName} not found — cannot delete gamertag row for {DiscordId}", sheetName, discordId);
            return false;
        }
        var sheetId = sheet.Properties.SheetId ?? 0;

        var deleteRequest = new Request
        {
            DeleteDimension = new DeleteDimensionRequest
            {
                Range = new DimensionRange
                {
                    SheetId    = sheetId,
                    Dimension  = "ROWS",
                    StartIndex = rowIndex,     // inclusive, 0-based
                    EndIndex   = rowIndex + 1, // exclusive
                }
            }
        };

        await service.Spreadsheets.BatchUpdate(
            new BatchUpdateSpreadsheetRequest { Requests = new List<Request> { deleteRequest } },
            spreadsheetId).ExecuteAsync();

        _logger.LogInformation("Deleted gamertag row {Row} for departing member {DiscordId}", rowIndex + 1, discordId);
        return true;
    }

    /// <summary>
    /// Reconciles the roster sheet against the current guild membership: removes
    /// rows for members who are no longer present. Used by the offline-gap
    /// reconciler to catch departures that happened while the bot was down (when
    /// Discord doesn't replay UserLeft).
    ///
    /// ── Legacy rows ──
    /// A row is only ever deleted when column A holds a parseable Discord ID that
    /// is NOT in <paramref name="currentMemberIds"/>. Rows with no ID (blank
    /// column A, or a legacy name-in-A row) are COUNTED but never deleted — there
    /// is no reliable key to confirm they belong to a departed member, and
    /// matching by display name risks deleting the wrong person. Those rows
    /// become reconcilable once that member re-runs /gamertags (which backfills
    /// their ID).
    ///
    /// ── Safety brake ──
    /// If the number of departed candidates exceeds <paramref name="maxDeletions"/>,
    /// the sweep deletes NOTHING and reports Aborted — a guard against a botched
    /// member-list fetch making most of the roster look departed. The caller is
    /// expected to also verify the member list is non-empty before calling.
    /// </summary>
    public async Task<GamertagReconcileResult> ReconcileGamertagsAsync(
        IReadOnlySet<ulong> currentMemberIds, int maxDeletions)
    {
        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        var spreadsheetId = _config.GoogleSpreadsheetId;
        var sheetName     = _config.GoogleSheetName;
        var range         = $"{sheetName}!A:H";

        var getResponse = await service.Spreadsheets.Values.Get(spreadsheetId, range).ExecuteAsync();
        var values = getResponse.Values;
        if (values is null || values.Count <= 1)
            return new GamertagReconcileResult(0, 0, 0, 0, Aborted: false);

        var toDelete      = new List<int>(); // 0-based sheet row indices
        var legacySkipped = 0;
        var kept          = 0;

        // Row 0 is the header — start at 1.
        for (var i = 1; i < values.Count; i++)
        {
            var row  = values[i];
            var colA = row.Count > 0 ? row[0]?.ToString() : null;

            if (ulong.TryParse(colA, out var id))
            {
                if (currentMemberIds.Contains(id)) kept++;
                else toDelete.Add(i);
            }
            else
            {
                // No parseable Discord ID — legacy / name-only row. Never auto-delete.
                legacySkipped++;
            }
        }

        if (toDelete.Count == 0)
            return new GamertagReconcileResult(0, kept, legacySkipped, 0, Aborted: false);

        if (toDelete.Count > maxDeletions)
        {
            _logger.LogWarning(
                "Gamertag reconcile: {Count} rows look departed, over the safety cap of {Cap}. " +
                "Deleting nothing this sweep — likely an incomplete guild member list. Verify, then " +
                "re-run or clean up manually.",
                toDelete.Count, maxDeletions);
            return new GamertagReconcileResult(0, kept, legacySkipped, toDelete.Count, Aborted: true);
        }

        // DeleteDimension needs the numeric sheet ID.
        var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
        var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == sheetName);
        if (sheet is null)
        {
            _logger.LogWarning("Sheet {SheetName} not found — gamertag reconcile aborted", sheetName);
            return new GamertagReconcileResult(0, kept, legacySkipped, toDelete.Count, Aborted: true);
        }
        var sheetId = sheet.Properties.SheetId ?? 0;

        // Delete bottom-up (highest index first): requests apply sequentially, so
        // removing a lower row would shift the indices of higher rows. Descending
        // order keeps every remaining index valid.
        var requests = toDelete
            .OrderByDescending(idx => idx)
            .Select(idx => new Request
            {
                DeleteDimension = new DeleteDimensionRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId    = sheetId,
                        Dimension  = "ROWS",
                        StartIndex = idx,
                        EndIndex   = idx + 1,
                    }
                }
            })
            .ToList();

        await service.Spreadsheets.BatchUpdate(
            new BatchUpdateSpreadsheetRequest { Requests = requests }, spreadsheetId).ExecuteAsync();

        _logger.LogInformation(
            "Gamertag reconcile: removed {Deleted} departed row(s); kept {Kept}; skipped {Legacy} legacy/no-ID row(s)",
            toDelete.Count, kept, legacySkipped);

        return new GamertagReconcileResult(toDelete.Count, kept, legacySkipped, toDelete.Count, Aborted: false);
    }

    /// <summary>
    /// Returns every roster row whose column A is not a parseable Discord ID —
    /// i.e. legacy/manually-added rows that the ID-keyed reconciler can't manage.
    /// Fully blank rows are skipped. Read-only; used by the /gamertag-backfill-ids
    /// command to propose name → ID matches.
    /// </summary>
    public async Task<List<LegacyGamertagRow>> GetLegacyGamertagRowsAsync()
    {
        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        var range = $"{_config.GoogleSheetName}!A:H";
        var resp = await service.Spreadsheets.Values.Get(_config.GoogleSpreadsheetId, range).ExecuteAsync();

        var result = new List<LegacyGamertagRow>();
        if (resp.Values is null) return result;

        // Skip the header row (index 0).
        for (var i = 1; i < resp.Values.Count; i++)
        {
            var row  = resp.Values[i];
            var colA = row.Count > 0 ? row[0]?.ToString() ?? "" : "";
            var colB = row.Count > 1 ? row[1]?.ToString() ?? "" : "";

            if (ulong.TryParse(colA, out _)) continue;            // already has an ID
            if (string.IsNullOrWhiteSpace(colA) && string.IsNullOrWhiteSpace(colB)) continue; // blank row

            result.Add(new LegacyGamertagRow(i + 1, colA, colB)); // i+1 = 1-based sheet row
        }

        return result;
    }

    /// <summary>
    /// Writes Discord IDs into column A for the given rows (keyed by 1-based sheet
    /// row number). ONLY column A is touched — every other cell (name, tags) is
    /// left exactly as-is — so this is safe to run against rows of unknown layout.
    /// Batched into one API call. Returns the number of cells written.
    /// </summary>
    public async Task<int> BackfillGamertagIdsAsync(IReadOnlyDictionary<int, ulong> rowToId)
    {
        if (rowToId.Count == 0) return 0;

        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        var sheetName = _config.GoogleSheetName;
        var data = rowToId.Select(kv => new ValueRange
        {
            Range  = $"{sheetName}!A{kv.Key}",
            Values = new List<IList<object>> { new List<object> { kv.Value.ToString() } }
        }).ToList();

        var body = new BatchUpdateValuesRequest
        {
            ValueInputOption = "USER_ENTERED",
            Data = data
        };

        await service.Spreadsheets.Values.BatchUpdate(body, _config.GoogleSpreadsheetId).ExecuteAsync();

        _logger.LogInformation("Backfilled {Count} Discord ID(s) into the gamertag roster", rowToId.Count);
        return rowToId.Count;
    }

    /// <summary>
    /// Looks up gamertags for a Discord user by ID, with fallback to name match for legacy rows.
    /// Returns null if no row is found.
    /// Columns: Discord ID | Discord Name | EA | Steam | PSN | Xbox | Embark | Bungie
    /// </summary>
    public async Task<GamertagLookupResult?> LookupGamertagsAsync(ulong discordId, string? fallbackName = null)
    {
        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        var spreadsheetId = _config.GoogleSpreadsheetId;
        var sheetName = _config.GoogleSheetName;
        var range = $"{sheetName}!A:H";

        var getRequest = service.Spreadsheets.Values.Get(spreadsheetId, range);
        var getResponse = await getRequest.ExecuteAsync();

        if (getResponse.Values is null) return null;

        // First pass: match by Discord ID in column A
        foreach (var row in getResponse.Values)
        {
            if (row.Count > 0 && string.Equals(row[0]?.ToString(), discordId.ToString(), StringComparison.Ordinal))
            {
                return RowToResult(row);
            }
        }

        // Second pass: fall back to matching by Discord Name (legacy rows without ID)
        if (!string.IsNullOrWhiteSpace(fallbackName))
        {
            foreach (var row in getResponse.Values)
            {
                var colA = row.Count > 0 ? row[0]?.ToString() ?? "" : "";
                var colB = row.Count > 1 ? row[1]?.ToString() ?? "" : "";

                if (string.Equals(colB, fallbackName, StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(colA, fallbackName, StringComparison.OrdinalIgnoreCase) && !ulong.TryParse(colA, out _)))
                {
                    return RowToResult(row);
                }
            }
        }

        return null;

        static GamertagLookupResult RowToResult(IList<object> row)
        {
            string Cell(int i) => row.Count > i ? row[i]?.ToString() ?? "" : "";
            return new GamertagLookupResult(
                DiscordName: Cell(1),
                EA: Cell(2),
                Steam: Cell(3),
                PSN: Cell(4),
                Xbox: Cell(5),
                Embark: Cell(6),
                Bungie: Cell(7));
        }
    }

    /// <summary>
    /// Sorts all data rows (excluding the header) alphabetically by the second column (Discord Name).
    /// </summary>
    private async Task SortSheetBySecondColumnAsync(SheetsService service, string spreadsheetId, string sheetName)
    {
        // Get the sheet ID by name
        var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
        var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == sheetName);
        if (sheet is null)
        {
            _logger.LogWarning("Sheet {SheetName} not found — skipping sort", sheetName);
            return;
        }

        var sheetId = sheet.Properties.SheetId ?? 0;
        var rowCount = sheet.Properties.GridProperties.RowCount ?? 1000;
        var colCount = sheet.Properties.GridProperties.ColumnCount ?? 8;

        var sortRequest = new Request
        {
            SortRange = new SortRangeRequest
            {
                Range = new GridRange
                {
                    SheetId = sheetId,
                    StartRowIndex = 1, // skip header row
                    EndRowIndex = rowCount,
                    StartColumnIndex = 0,
                    EndColumnIndex = colCount
                },
                SortSpecs = new List<SortSpec>
                {
                    new SortSpec
                    {
                        DimensionIndex = 1, // column B (Discord Name)
                        SortOrder = "ASCENDING"
                    }
                }
            }
        };

        var batchUpdate = new BatchUpdateSpreadsheetRequest
        {
            Requests = new List<Request> { sortRequest }
        };

        var response = await service.Spreadsheets.BatchUpdate(batchUpdate, spreadsheetId).ExecuteAsync();
        _logger.LogInformation("Sorted sheet {SheetName} alphabetically by Discord Name (replies: {Count})",
            sheetName, response.Replies?.Count ?? 0);
    }

    /// <summary>
    /// Ensures the header row exists in the sheet. Call once on startup.
    /// </summary>
    public async Task EnsureHeaderRowAsync()
    {
        try
        {
            var credential = GoogleCredential
                .FromFile(_config.GoogleCredentialsPath)
                .CreateScoped(SheetsService.Scope.Spreadsheets);

            using var service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "ClanGuardBot"
            });

            var range = $"{_config.GoogleSheetName}!A1:H1";
            var getRequest = service.Spreadsheets.Values.Get(_config.GoogleSpreadsheetId, range);
            var response = await getRequest.ExecuteAsync();

            if (response.Values is null || response.Values.Count == 0)
            {
                var header = new List<object> { "Discord ID", "Discord Name", "EA", "Steam", "PSN", "Xbox", "Embark", "Bungie" };
                var body = new ValueRange { Values = new List<IList<object>> { header } };
                var updateRequest = service.Spreadsheets.Values.Update(body, _config.GoogleSpreadsheetId, range);
                updateRequest.ValueInputOption =
                    SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                await updateRequest.ExecuteAsync();

                _logger.LogInformation("Created header row in Gamertags sheet");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure header row in Google Sheet (sheet may not be configured yet)");
        }
    }

    /// <summary>
    /// Writes the full roster to the Roster sheet, replacing all existing data.
    /// </summary>
    public async Task WriteRosterAsync(string guildName, List<RosterRow> rows)
    {
        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        // The default HttpClient timeout (100s) is not enough when we hit the
        // first post-fix run, because we may be cleaning up thousands of
        // accumulated conditional-format rules on the roster sheet. Bump to
        // 5 minutes which comfortably handles very large backlogs. Steady
        // state runs finish in a few seconds regardless.
        service.HttpClient.Timeout = TimeSpan.FromMinutes(5);

        var spreadsheetId = string.IsNullOrWhiteSpace(_config.RosterSpreadsheetId)
            ? _config.GoogleSpreadsheetId
            : _config.RosterSpreadsheetId;
        var sheetName = _config.RosterSheetName;

        // Ensure the Roster tab exists
        await EnsureSheetTabExistsAsync(service, spreadsheetId, sheetName);

        // Build the data: header + rows
        var allRows = new List<IList<object>>();

        // Header
        allRows.Add(new List<object>
        {
            "Discord Name", "Username", "Rank", "Roles", "Join Date",
            "Messages", "Voice Hours", "Window", "AWOL",
            "Time in Rank", "Last Events VC", "Events At Rank", "Promotable",
            $"Last Updated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"
        });

        // Data rows
        foreach (var row in rows)
        {
            var timeInRank = row.RankSince.HasValue
                ? FormatDuration(DateTime.UtcNow - row.RankSince.Value)
                : "—";

            var lastEvents = row.LastEventsVc.HasValue
                ? row.LastEventsVc.Value.ToString("yyyy-MM-dd")
                : "Never";

            allRows.Add(new List<object>
            {
                row.DiscordName,
                row.Username,
                row.Rank,
                row.Roles,
                row.JoinDate?.ToString("yyyy-MM-dd") ?? "Unknown",
                row.Messages,
                row.VoiceHours.ToString("F1"),
                $"{row.WindowDays}d",
                row.IsAwol ? "YES" : "",
                timeInRank,
                lastEvents,
                row.EventsAtRank,
                row.IsPromotable ? "YES" : ""
            });
        }

        // Clear existing data and write fresh. NOTE: We clear A:N only — any
        // user-added columns to the right (e.g. "Seed Events" and "Seed Applied"
        // for the /seed-promotion-credit one-time backfill) are preserved.
        var fullRange = $"{sheetName}!A1:N{allRows.Count + 10}";
        var clearRequest = service.Spreadsheets.Values.Clear(
            new ClearValuesRequest(), spreadsheetId, fullRange);
        await clearRequest.ExecuteAsync();

        var writeRange = $"{sheetName}!A1:N{allRows.Count}";
        var body = new ValueRange { Values = allRows };
        var updateRequest = service.Spreadsheets.Values.Update(body, spreadsheetId, writeRange);
        updateRequest.ValueInputOption =
            SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
        await updateRequest.ExecuteAsync();

        // ── Apply formatting ────────────────────────────────────────────
        //
        // Historical note: an earlier version of this method added one
        // conditional-format rule per data row on every export without ever
        // cleaning up the previous run's rules. All N rules were identical
        // (same formula, same range, same color), so they were redundant —
        // one rule covers all alternating rows via the MOD(ROW(),2)=0
        // formula. After months of nightly runs, thousands of duplicate
        // rules accumulated on the sheet and the format batch started
        // timing out the 100s HttpClient limit.
        //
        // This version:
        //   1. Deletes ALL existing conditional-format rules (in chunks so
        //      the cleanup itself doesn't time out on the first run post-
        //      fix, when the backlog may be very large).
        //   2. Adds exactly ONE alternating-row-shading rule.
        //   3. Applies the dark header-row styling.
        //
        // Steady state after the first run: at most 1 existing rule to
        // delete + 1 rule to add + 1 header format = 3 requests per export.

        var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
        var rosterSheet = spreadsheet.Sheets.First(s => s.Properties.Title == sheetName);
        var sheetId = rosterSheet.Properties.SheetId ?? 0;
        var existingRuleCount = rosterSheet.ConditionalFormats?.Count ?? 0;

        if (existingRuleCount > 0)
        {
            _logger.LogInformation(
                "Roster sheet has {Count} existing conditional-format rule(s) — deleting before re-applying fresh formatting",
                existingRuleCount);

            // Build all deletes from highest index to lowest so that the
            // indices remain valid as the sheet processes the batch.
            var deleteRequests = new List<Request>();
            for (int idx = existingRuleCount - 1; idx >= 0; idx--)
            {
                deleteRequests.Add(new Request
                {
                    DeleteConditionalFormatRule = new DeleteConditionalFormatRuleRequest
                    {
                        SheetId = sheetId,
                        Index = idx
                    }
                });
            }

            // Chunk deletes so no single batch stresses Google's servers.
            // An earlier version used 500 per chunk and hit 500 InternalServerError
            // responses from Sheets on the larger accumulated backlogs. 200 per
            // chunk keeps each batch well within comfortable processing time.
            // The per-chunk retry wrapper below handles any remaining transient
            // 5xx errors.
            const int deleteChunkSize = 200;
            var chunkStartTime = DateTime.UtcNow;
            for (int offset = 0; offset < deleteRequests.Count; offset += deleteChunkSize)
            {
                var chunk = deleteRequests
                    .Skip(offset)
                    .Take(deleteChunkSize)
                    .ToList();

                var chunkBegin = DateTime.UtcNow;
                await ExecuteWithRetryAsync(
                    () => service.Spreadsheets.BatchUpdate(
                        new BatchUpdateSpreadsheetRequest { Requests = chunk },
                        spreadsheetId).ExecuteAsync(),
                    $"delete chunk {offset / deleteChunkSize + 1}");

                var chunkDuration = DateTime.UtcNow - chunkBegin;
                _logger.LogInformation(
                    "Deleted conditional-format rules {From}-{To} of {Total} in {Duration:F1}s",
                    offset + 1,
                    Math.Min(offset + chunk.Count, deleteRequests.Count),
                    deleteRequests.Count,
                    chunkDuration.TotalSeconds);
            }

            var totalDuration = DateTime.UtcNow - chunkStartTime;
            _logger.LogInformation(
                "Conditional-format cleanup complete: removed {Count} rule(s) in {Duration:F1}s total",
                existingRuleCount, totalDuration.TotalSeconds);
        }

        // Single rule for alternating shading + header formatting.
        var formatRequests = new List<Request>
        {
            new Request
            {
                AddConditionalFormatRule = new AddConditionalFormatRuleRequest
                {
                    Rule = new ConditionalFormatRule
                    {
                        Ranges = new List<GridRange>
                        {
                            new GridRange
                            {
                                SheetId = sheetId,
                                StartRowIndex = 1,
                                EndRowIndex = rows.Count + 1,
                                StartColumnIndex = 0,
                                EndColumnIndex = 13
                            }
                        },
                        BooleanRule = new BooleanRule
                        {
                            Condition = new BooleanCondition
                            {
                                Type = "CUSTOM_FORMULA",
                                Values = new List<ConditionValue>
                                {
                                    new ConditionValue { UserEnteredValue = "=MOD(ROW(),2)=0" }
                                }
                            },
                            Format = new CellFormat
                            {
                                BackgroundColor = new Color { Red = 0.95f, Green = 0.95f, Blue = 0.95f }
                            }
                        }
                    },
                    Index = 0
                }
            },

            // Dark header styling
            new Request
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange
                    {
                        SheetId = sheetId,
                        StartRowIndex = 0,
                        EndRowIndex = 1,
                        StartColumnIndex = 0,
                        EndColumnIndex = 13
                    },
                    Cell = new CellData
                    {
                        UserEnteredFormat = new CellFormat
                        {
                            BackgroundColor = new Color { Red = 0.2f, Green = 0.2f, Blue = 0.2f, Alpha = 1f },
                            TextFormat = new TextFormat
                            {
                                Bold = true,
                                ForegroundColor = new Color { Red = 1f, Green = 1f, Blue = 1f, Alpha = 1f }
                            }
                        }
                    },
                    Fields = "userEnteredFormat(backgroundColor,textFormat)"
                }
            }
        };

        await ExecuteWithRetryAsync(
            () => service.Spreadsheets.BatchUpdate(
                new BatchUpdateSpreadsheetRequest { Requests = formatRequests },
                spreadsheetId).ExecuteAsync(),
            "apply format rules");

        _logger.LogInformation("Wrote {Count} roster rows to sheet {SheetName}", rows.Count, sheetName);
    }

    /// <summary>
    /// Retries a Google Sheets API call with exponential backoff on transient
    /// 5xx server errors. Google's own documentation recommends retrying 500,
    /// 502, 503, and 504 responses, which tend to appear when their servers
    /// are temporarily overloaded or processing stressful operations (such as
    /// mass deletes on a sheet with thousands of accumulated conditional-format
    /// rules — the exact scenario that motivated this helper).
    ///
    /// Non-transient errors (4xx, auth failures, validation errors) are NOT
    /// retried — retrying those would just repeat the same failure, and they
    /// indicate a bug or misconfiguration that needs attention rather than
    /// patience.
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationDescription,
        int maxAttempts = 5)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Google.GoogleApiException ex) when (IsTransientServerError(ex.HttpStatusCode))
            {
                lastException = ex;

                if (attempt == maxAttempts)
                {
                    _logger.LogError(
                        "Google Sheets API returned {Status} during '{Op}' on final attempt {Attempt}/{Max}. Giving up.",
                        ex.HttpStatusCode, operationDescription, attempt, maxAttempts);
                    break;
                }

                // Exponential backoff: 1s, 2s, 4s, 8s between attempts 1→2, 2→3, 3→4, 4→5.
                var delayMs = (int)(Math.Pow(2, attempt - 1) * 1000);
                _logger.LogWarning(
                    "Google Sheets API returned {Status} during '{Op}' on attempt {Attempt}/{Max}. Retrying in {Delay}ms...",
                    ex.HttpStatusCode, operationDescription, attempt, maxAttempts, delayMs);
                await Task.Delay(delayMs);
            }
        }

        // Exhausted retries — rethrow the last transient error so the caller
        // sees a meaningful failure rather than a null-reference crash.
        throw lastException!;
    }

    private static bool IsTransientServerError(System.Net.HttpStatusCode status) =>
        status == System.Net.HttpStatusCode.InternalServerError   // 500
        || status == System.Net.HttpStatusCode.BadGateway          // 502
        || status == System.Net.HttpStatusCode.ServiceUnavailable  // 503
        || status == System.Net.HttpStatusCode.GatewayTimeout;     // 504

    // ─── Seed promotion credit support ──────────────────────────────────
    //
    // The roster sheet may optionally have two human-edited columns to the
    // right of the bot-managed columns A-N:
    //   "Seed Events"  — an officer enters the event count from the manual
    //                    tracking spreadsheet for each person at their
    //                    current rank.
    //   "Seed Applied" — the bot writes "YES | timestamp" after the seed has
    //                    been applied to the DB, so re-runs don't re-apply.
    //
    // These columns are temporary (for the one-time transition from the
    // spreadsheet to bot-tracked events). Once the transition is complete,
    // the columns can be deleted from the sheet and will not be rewritten by
    // WriteRosterAsync (which only clears A:N).

    /// <summary>
    /// Per-row data read from the roster sheet for seed-credit processing.
    /// </summary>
    public record RosterSeedRow(
        int RowNumber,         // 1-indexed sheet row (header is row 1)
        string DiscordName,    // column A value
        string Username,       // column B value
        string Rank,           // column C value
        int? SeedEvents,       // parsed from the "Seed Events" column (null if empty or non-integer)
        string SeedApplied);   // raw string from the "Seed Applied" column ("" if empty)

    /// <summary>
    /// Result of a ReadRosterSeedRowsAsync call.
    /// </summary>
    public record RosterSeedReadResult(
        bool Success,
        string? Error,
        int SeedEventsColumnIndex,   // 0-indexed; -1 if not found
        int SeedAppliedColumnIndex,  // 0-indexed; -1 if not found
        List<RosterSeedRow> Rows);

    /// <summary>
    /// Reads the roster sheet, locating the "Seed Events" and "Seed Applied"
    /// columns by header text (anywhere in the header row). Returns one
    /// RosterSeedRow per data row. Rows are returned in sheet order — callers
    /// filter down to rows with a seed value and an empty "Seed Applied" cell.
    ///
    /// Returns Success=false with an Error explanation if the headers are not
    /// present; the /seed-promotion-credit handler reports this to the user.
    /// </summary>
    public async Task<RosterSeedReadResult> ReadRosterSeedRowsAsync()
    {
        var emptyRows = new List<RosterSeedRow>();

        try
        {
            var credential = GoogleCredential
                .FromFile(_config.GoogleCredentialsPath)
                .CreateScoped(SheetsService.Scope.Spreadsheets);

            using var service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "ClanGuardBot"
            });

            var spreadsheetId = string.IsNullOrWhiteSpace(_config.RosterSpreadsheetId)
                ? _config.GoogleSpreadsheetId
                : _config.RosterSpreadsheetId;
            var sheetName = _config.RosterSheetName;

            // Read out to column Z — gives plenty of headroom past N for
            // manually-added seed columns regardless of where the officer
            // placed them.
            var range = $"{sheetName}!A1:Z";
            var getRequest = service.Spreadsheets.Values.Get(spreadsheetId, range);
            var response = await getRequest.ExecuteAsync();

            if (response.Values is null || response.Values.Count == 0)
            {
                return new RosterSeedReadResult(false,
                    $"Roster sheet '{sheetName}' is empty. Run /roster-export first.",
                    -1, -1, emptyRows);
            }

            var header = response.Values[0];
            int seedEventsIdx = -1;
            int seedAppliedIdx = -1;

            for (int i = 0; i < header.Count; i++)
            {
                var cell = (header[i]?.ToString() ?? "").Trim();
                if (seedEventsIdx < 0 && string.Equals(cell, "Seed Events", StringComparison.OrdinalIgnoreCase))
                    seedEventsIdx = i;
                else if (seedAppliedIdx < 0 && string.Equals(cell, "Seed Applied", StringComparison.OrdinalIgnoreCase))
                    seedAppliedIdx = i;
            }

            if (seedEventsIdx < 0 || seedAppliedIdx < 0)
            {
                return new RosterSeedReadResult(false,
                    "Could not find 'Seed Events' and/or 'Seed Applied' columns in the roster header. Add those two column headers to the right of the bot-managed columns (e.g. columns O and P) and try again.",
                    seedEventsIdx, seedAppliedIdx, emptyRows);
            }

            var rows = new List<RosterSeedRow>();

            for (int i = 1; i < response.Values.Count; i++)
            {
                var row = response.Values[i];

                string discordName = row.Count > 0 ? (row[0]?.ToString() ?? "") : "";
                string username    = row.Count > 1 ? (row[1]?.ToString() ?? "") : "";
                string rank        = row.Count > 2 ? (row[2]?.ToString() ?? "") : "";

                // Guard the column accesses — a row may have fewer cells than
                // the header if trailing cells are blank.
                string seedRaw     = row.Count > seedEventsIdx  ? (row[seedEventsIdx]?.ToString()  ?? "") : "";
                string appliedRaw  = row.Count > seedAppliedIdx ? (row[seedAppliedIdx]?.ToString() ?? "") : "";

                int? seedEvents = null;
                if (!string.IsNullOrWhiteSpace(seedRaw)
                    && int.TryParse(seedRaw.Trim(), out var parsed)
                    && parsed >= 0)
                {
                    seedEvents = parsed;
                }

                rows.Add(new RosterSeedRow(
                    RowNumber: i + 1, // 1-indexed (row 1 is header, so data starts at row 2)
                    DiscordName: discordName,
                    Username: username,
                    Rank: rank,
                    SeedEvents: seedEvents,
                    SeedApplied: appliedRaw.Trim()));
            }

            return new RosterSeedReadResult(true, null, seedEventsIdx, seedAppliedIdx, rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read roster seed rows");
            return new RosterSeedReadResult(false, $"Error reading roster sheet: {ex.Message}", -1, -1, emptyRows);
        }
    }

    /// <summary>
    /// Batch-writes values into the "Seed Applied" column for the given rows.
    /// Uses BatchUpdate so all cells are written in a single API round trip.
    /// </summary>
    /// <param name="seedAppliedColumnIndex">0-indexed column number as returned
    /// from ReadRosterSeedRowsAsync.</param>
    /// <param name="rowValues">1-indexed sheet row → cell value.</param>
    public async Task WriteSeedAppliedCellsAsync(
        int seedAppliedColumnIndex,
        IReadOnlyDictionary<int, string> rowValues)
    {
        if (rowValues.Count == 0) return;

        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        var spreadsheetId = string.IsNullOrWhiteSpace(_config.RosterSpreadsheetId)
            ? _config.GoogleSpreadsheetId
            : _config.RosterSpreadsheetId;
        var sheetName = _config.RosterSheetName;

        var columnLetter = ColumnIndexToLetter(seedAppliedColumnIndex);

        var data = new List<ValueRange>();
        foreach (var kv in rowValues)
        {
            data.Add(new ValueRange
            {
                Range = $"{sheetName}!{columnLetter}{kv.Key}",
                Values = new List<IList<object>> { new List<object> { kv.Value } }
            });
        }

        var batchBody = new BatchUpdateValuesRequest
        {
            ValueInputOption = "USER_ENTERED",
            Data = data
        };

        await service.Spreadsheets.Values.BatchUpdate(batchBody, spreadsheetId).ExecuteAsync();

        _logger.LogInformation("Wrote {Count} Seed Applied cells to {Sheet} (column {Column})",
            rowValues.Count, sheetName, columnLetter);
    }

    /// <summary>
    /// Converts a 0-indexed column number to A1-notation letter(s).
    /// 0 → A, 1 → B, ..., 25 → Z, 26 → AA, 27 → AB, etc.
    /// </summary>
    private static string ColumnIndexToLetter(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));

        var sb = new System.Text.StringBuilder();
        index++; // switch to 1-indexed for the math
        while (index > 0)
        {
            var rem = (index - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            index = (index - 1) / 26;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Creates a sheet tab if it doesn't already exist in the spreadsheet.
    /// </summary>
    private async Task EnsureSheetTabExistsAsync(SheetsService service, string spreadsheetId, string sheetName)
    {
        var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
        if (spreadsheet.Sheets.Any(s => s.Properties.Title == sheetName))
            return;

        var addSheetRequest = new Request
        {
            AddSheet = new AddSheetRequest
            {
                Properties = new SheetProperties { Title = sheetName }
            }
        };

        var batchUpdate = new BatchUpdateSpreadsheetRequest
        {
            Requests = new List<Request> { addSheetRequest }
        };

        await service.Spreadsheets.BatchUpdate(batchUpdate, spreadsheetId).ExecuteAsync();
        _logger.LogInformation("Created sheet tab {SheetName}", sheetName);
    }

    /// <summary>
    /// Appends a row to the Recruit Log sheet.
    /// Columns: Recruit | Date | Time (UTC) | Logged By
    /// </summary>
    public async Task WriteRecruitLogAsync(string recruitName, string loggedBy, DateTime timestampUtc)
    {
        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        using var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });

        var spreadsheetId = string.IsNullOrWhiteSpace(_config.RecruitSpreadsheetId)
            ? _config.GoogleSpreadsheetId
            : _config.RecruitSpreadsheetId;
        var sheetName = _config.RecruitSheetName;

        // Ensure the tab exists
        await EnsureSheetTabExistsAsync(service, spreadsheetId, sheetName);

        var range = $"'{sheetName}'!A:D";

        var newRow = new List<object>
        {
            recruitName,
            timestampUtc.ToString("yyyy-MM-dd"),
            timestampUtc.ToString("HH:mm:ss"),
            loggedBy
        };

        var appendBody = new ValueRange { Values = new List<IList<object>> { newRow } };
        var appendRequest = service.Spreadsheets.Values.Append(appendBody, spreadsheetId, range);
        appendRequest.ValueInputOption =
            SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.RAW;
        appendRequest.InsertDataOption =
            SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
        var appendResponse = await appendRequest.ExecuteAsync();

        // Clear formatting on the appended row so the header's teal doesn't bleed down
        var updatedRange = appendResponse.Updates?.UpdatedRange;
        if (updatedRange is not null)
        {
            var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
            var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == sheetName);
            if (sheet is not null)
            {
                var sheetId = sheet.Properties.SheetId ?? 0;

                // Parse the row number from the updated range (e.g. "'Recruit Log'!A2:D2")
                var match = System.Text.RegularExpressions.Regex.Match(updatedRange, @"(\d+)$");
                if (match.Success && int.TryParse(match.Value, out var rowNumber))
                {
                    var clearFormat = new BatchUpdateSpreadsheetRequest
                    {
                        Requests = new List<Request>
                        {
                            new Request
                            {
                                RepeatCell = new RepeatCellRequest
                                {
                                    Range = new GridRange
                                    {
                                        SheetId = sheetId,
                                        StartRowIndex = rowNumber - 1, // 0-indexed
                                        EndRowIndex = rowNumber,
                                        StartColumnIndex = 0,
                                        EndColumnIndex = 4
                                    },
                                    Cell = new CellData
                                    {
                                        UserEnteredFormat = new CellFormat
                                        {
                                            BackgroundColor = new Color
                                            {
                                                Red = 1f, Green = 1f, Blue = 1f, Alpha = 1f
                                            },
                                            TextFormat = new TextFormat
                                            {
                                                Bold = false,
                                                ForegroundColor = new Color
                                                {
                                                    Red = 0f, Green = 0f, Blue = 0f, Alpha = 1f
                                                }
                                            }
                                        }
                                    },
                                    Fields = "userEnteredFormat(backgroundColor,textFormat)"
                                }
                            }
                        }
                    };
                    await service.Spreadsheets.BatchUpdate(clearFormat, spreadsheetId).ExecuteAsync();
                }
            }
        }

        _logger.LogInformation("Appended recruit log: {RecruitName} by {LoggedBy}", recruitName, loggedBy);
    }

    /// <summary>
    /// Ensures the header row exists in the Recruit Log sheet with formatting. Call once on startup.
    /// </summary>
    public async Task EnsureRecruitHeaderRowAsync()
    {
        try
        {
            var credential = GoogleCredential
                .FromFile(_config.GoogleCredentialsPath)
                .CreateScoped(SheetsService.Scope.Spreadsheets);

            using var service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "ClanGuardBot"
            });

            var spreadsheetId = string.IsNullOrWhiteSpace(_config.RecruitSpreadsheetId)
                ? _config.GoogleSpreadsheetId
                : _config.RecruitSpreadsheetId;
            var sheetName = _config.RecruitSheetName;

            // Ensure the tab exists
            await EnsureSheetTabExistsAsync(service, spreadsheetId, sheetName);

            var range = $"'{sheetName}'!A1:D1";
            var getRequest = service.Spreadsheets.Values.Get(spreadsheetId, range);
            var response = await getRequest.ExecuteAsync();

            if (response.Values is null || response.Values.Count == 0)
            {
                // Write header values
                var header = new List<object> { "Recruit", "Date", "Time (UTC)", "Logged By" };
                var body = new ValueRange { Values = new List<IList<object>> { header } };
                var updateRequest = service.Spreadsheets.Values.Update(body, spreadsheetId, range);
                updateRequest.ValueInputOption =
                    SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                await updateRequest.ExecuteAsync();

                // Apply teal background + white bold text formatting
                var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync();
                var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == sheetName);
                if (sheet is not null)
                {
                    var sheetId = sheet.Properties.SheetId ?? 0;

                    var formatRequest = new BatchUpdateSpreadsheetRequest
                    {
                        Requests = new List<Request>
                        {
                            new Request
                            {
                                RepeatCell = new RepeatCellRequest
                                {
                                    Range = new GridRange
                                    {
                                        SheetId = sheetId,
                                        StartRowIndex = 0,
                                        EndRowIndex = 1,
                                        StartColumnIndex = 0,
                                        EndColumnIndex = 4
                                    },
                                    Cell = new CellData
                                    {
                                        UserEnteredFormat = new CellFormat
                                        {
                                            BackgroundColor = new Color
                                            {
                                                Red = 0.0f,
                                                Green = 0.502f,
                                                Blue = 0.502f,
                                                Alpha = 1f
                                            },
                                            TextFormat = new TextFormat
                                            {
                                                Bold = true,
                                                ForegroundColor = new Color
                                                {
                                                    Red = 1f,
                                                    Green = 1f,
                                                    Blue = 1f,
                                                    Alpha = 1f
                                                }
                                            }
                                        }
                                    },
                                    Fields = "userEnteredFormat(backgroundColor,textFormat)"
                                }
                            }
                        }
                    };
                    await service.Spreadsheets.BatchUpdate(formatRequest, spreadsheetId).ExecuteAsync();
                }

                _logger.LogInformation("Created and formatted header row in Recruit Log sheet");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure header row in Recruit Log sheet (sheet may not be configured yet)");
        }
    }

    /// <summary>Formats a TimeSpan as a human-readable duration like "45d" or "3mo 12d".</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        var totalDays = (int)duration.TotalDays;
        if (totalDays < 1) return "< 1d";
        if (totalDays < 30) return $"{totalDays}d";

        var months = totalDays / 30;
        var remainingDays = totalDays % 30;
        if (months < 12)
            return remainingDays > 0 ? $"{months}mo {remainingDays}d" : $"{months}mo";

        var years = months / 12;
        var remainingMonths = months % 12;
        return remainingMonths > 0 ? $"{years}y {remainingMonths}mo" : $"{years}y";
    }
}