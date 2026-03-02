using ArchiveAutomator.Models;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.IO;

namespace ArchiveAutomator.Services;

public class ExcelParserService
{
    /// <summary>
    /// Parses a .csv or .xlsx file and returns all rows where the status column
    /// matches <paramref name="triggerValue"/> (case-insensitive trim).
    /// </summary>
    public List<JobItem> Parse(string filePath, ColumnMappings mapping)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".csv" => ParseCsv(filePath, mapping),
            ".xlsx" or ".xlsm" => ParseXlsx(filePath, mapping),
            _ => throw new NotSupportedException($"File type '{ext}' is not supported. Use .csv or .xlsx.")
        };
    }

    /// <summary>
    /// Reads the header row of a file and returns the column names.
    /// Used to populate the mapping ComboBoxes in the UI.
    /// </summary>
    public List<string> ReadHeaders(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".csv" => ReadCsvHeaders(filePath),
            ".xlsx" or ".xlsm" => ReadXlsxHeaders(filePath),
            _ => throw new NotSupportedException($"File type '{ext}' is not supported.")
        };
    }

    // ── CSV ────────────────────────────────────────────────────────────────

    private static List<string> ReadCsvHeaders(string filePath)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, config);
        csv.Read();
        csv.ReadHeader();
        return csv.HeaderRecord?.ToList() ?? new List<string>();
    }

    private static List<JobItem> ParseCsv(string filePath, ColumnMappings mapping)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            MissingFieldFound = null,
            BadDataFound = null
        };

        var results = new List<JobItem>();
        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, config);

        csv.Read();
        csv.ReadHeader();

        ValidateColumns(csv.HeaderRecord, mapping);

        while (csv.Read())
        {
            string status = csv.GetField(mapping.StatusColumn)?.Trim() ?? string.Empty;
            if (!string.Equals(status, mapping.TriggerValue, StringComparison.OrdinalIgnoreCase))
                continue;

            string jobNumber = csv.GetField(mapping.JobNumberColumn)?.Trim() ?? string.Empty;
            results.Add(new JobItem
            {
                JobNumber = jobNumber,
                FolderName = jobNumber,   // refined by UI/orchestrator if folder name differs
                Status = status,
                State = JobState.Pending
            });
        }

        return results;
    }

    // ── XLSX ───────────────────────────────────────────────────────────────

    private static List<string> ReadXlsxHeaders(string filePath)
    {
        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheets.First();
        var headerRow = ws.Row(1);
        var headers = new List<string>();

        foreach (var cell in headerRow.CellsUsed())
            headers.Add(cell.GetString().Trim());

        return headers;
    }

    private static List<JobItem> ParseXlsx(string filePath, ColumnMappings mapping)
    {
        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheets.First();

        // Build column index map from header row
        var headerRow = ws.Row(1);
        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
            columnIndex[cell.GetString().Trim()] = cell.Address.ColumnNumber;

        ValidateColumns(columnIndex.Keys, mapping);

        int jobCol = columnIndex[mapping.JobNumberColumn];
        int statusCol = columnIndex[mapping.StatusColumn];

        var results = new List<JobItem>();
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        for (int row = 2; row <= lastRow; row++)
        {
            string status = ws.Cell(row, statusCol).GetString().Trim();
            if (!string.Equals(status, mapping.TriggerValue, StringComparison.OrdinalIgnoreCase))
                continue;

            string jobNumber = ws.Cell(row, jobCol).GetString().Trim();
            if (string.IsNullOrEmpty(jobNumber)) continue;

            results.Add(new JobItem
            {
                JobNumber = jobNumber,
                FolderName = jobNumber,
                Status = status,
                State = JobState.Pending
            });
        }

        return results;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void ValidateColumns(IEnumerable<string>? headers, ColumnMappings mapping)
    {
        var set = new HashSet<string>(headers ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        if (!set.Contains(mapping.JobNumberColumn))
            throw new InvalidOperationException($"Column '{mapping.JobNumberColumn}' not found in file.");

        if (!set.Contains(mapping.StatusColumn))
            throw new InvalidOperationException($"Column '{mapping.StatusColumn}' not found in file.");
    }
}
