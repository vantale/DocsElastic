// Print the keys present in the first search row (helps tailor the parser)
static void DebugFirstRowKeys(string json)
{
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;

    System.Text.Json.JsonElement rows;

    // Shape A: PrimaryQueryResult.RelevantResults.Table.Rows
    if (root.TryGetProperty("PrimaryQueryResult", out var pqr) &&
        pqr.TryGetProperty("RelevantResults", out var rr) &&
        rr.TryGetProperty("Table", out var table) &&
        table.TryGetProperty("Rows", out var rowsEl))
    {
        if (rowsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            rows = rowsEl;
        else if (rowsEl.ValueKind == System.Text.Json.JsonValueKind.Object &&
                 rowsEl.TryGetProperty("results", out var res) &&
                 res.ValueKind == System.Text.Json.JsonValueKind.Array)
            rows = res;
        else
        {
            Console.WriteLine("[Search debug] Rows present but not an array.");
            return;
        }
    }
    // Shape B: d.query.PrimaryQueryResult.RelevantResults.Table.Rows
    else if (root.TryGetProperty("d", out var d) &&
             d.TryGetProperty("query", out var q) &&
             q.TryGetProperty("PrimaryQueryResult", out var pqr2) &&
             pqr2.TryGetProperty("RelevantResults", out var rr2) &&
             rr2.TryGetProperty("Table", out var table2) &&
             table2.TryGetProperty("Rows", out var rowsEl2))
    {
        if (rowsEl2.ValueKind == System.Text.Json.JsonValueKind.Array)
            rows = rowsEl2;
        else if (rowsEl2.ValueKind == System.Text.Json.JsonValueKind.Object &&
                 rowsEl2.TryGetProperty("results", out var res2) &&
                 res2.ValueKind == System.Text.Json.JsonValueKind.Array)
            rows = res2;
        else
        {
            Console.WriteLine("[Search debug] Rows present (verbose) but not an array.");
            return;
        }
    }
    else
    {
        Console.WriteLine("[Search debug] No PrimaryQueryResult → RelevantResults → Table → Rows found.");
        return;
    }

    if (rows.GetArrayLength() == 0)
    {
        Console.WriteLine("[Search debug] 0 rows.");
        return;
    }

    var row0 = rows[0];
    if (!row0.TryGetProperty("Cells", out var cellsEl))
    {
        Console.WriteLine("[Search debug] First row has no Cells.");
        return;
    }

    System.Text.Json.JsonElement cells;
    if (cellsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        cells = cellsEl;
    else if (cellsEl.ValueKind == System.Text.Json.JsonValueKind.Object &&
             cellsEl.TryGetProperty("results", out var resCells) &&
             resCells.ValueKind == System.Text.Json.JsonValueKind.Array)
        cells = resCells;
    else
    {
        Console.WriteLine("[Search debug] Cells present but not an array.");
        return;
    }

    var keys = new System.Collections.Generic.List<string>();
    for (int i = 0; i < cells.GetArrayLength(); i++)
    {
        var cell = cells[i];
        if (cell.TryGetProperty("Key", out var kEl))
            keys.Add(kEl.GetString() ?? "?");
    }
    Console.WriteLine("[Search debug] Keys in row0: " + string.Join(", ", keys));
}
