using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DataverseTableRowCounts;


class Program
{
    // TODO Enter your Dataverse environment's URL and logon info.
    static string url = "https://markicrmdev.crm.dynamics.com";
    static string userName = "michael.gernaey@markimicrowave.com";
    static string password = "Raven#2018";
    // This service connection string uses the info#2018" provided above.
    // The AppId and RedirectUri are provided for sample code testing.


    static void Main()
    {
        var connectionString = @"
            AuthType=OAuth;
            Url=https://markicrmdev.crm.dynamics.com;
            UserName=michael.gernaey@markimicrowave.com;
            Password=Raven#2018;
            AppId=51f81489-12ee-4a9e-aaae-a2591f45987d;
            RedirectUri=app://58145B91-0C36-4500-8554-080854F2AC97;
            LoginPrompt=Auto;";
        //ServiceClient implements IOrganizationService interface
        IOrganizationService service = new ServiceClient(connectionString);

        var response = (WhoAmIResponse)service.Execute(new WhoAmIRequest());




        //   ServiceClient svc = new ServiceClient(connectionString);


        Console.WriteLine($"User ID is {response.UserId}.");

        PrintNonVirtualTableCounts2(service);

        //TableCounts.Run(svc, true, @"D:\TableCounts");

        // Pause the console so it does not close.
        Console.WriteLine("Press the <Enter> key to exit.");
        Console.ReadLine();
    }



    // If you prefer, add: using Microsoft.Crm.Sdk.Messages; and remove the fully-qualified names below.

    static void PrintNonVirtualTableCounts2(
        IOrganizationService org,
        string countsCsvPath = null,
        string skippedCsvPath = null)
    {
        // Default file paths (next to the process working directory)
        countsCsvPath = Path.Combine(@"D:\TableCounts\", "dataverse_table_counts.csv");
        skippedCsvPath = Path.Combine(@"D:\TableCounts\", "dataverse_tables_skipped.csv");

        // 1) Get table metadata (tables only)
        var metaReq = new RetrieveAllEntitiesRequest
        {
            EntityFilters = EntityFilters.Entity,   // only table-level info for performance
            RetrieveAsIfPublished = false
        };
        var metaResp = (RetrieveAllEntitiesResponse)org.Execute(metaReq); // returns EntityMetadata[]

        // 2) Build a "safe" list:
        //    - exclude virtual tables
        //    - exclude M2M/intersection tables
        //    - prefer "user-facing" tables (CanCreateForms/CanCreateViews)
        IEnumerable<EntityMetadata> safeMeta = metaResp.EntityMetadata;

        // Exclude Virtual tables (preferred: TableType == "Virtual")
        bool tableTypeAvailable = metaResp.EntityMetadata.Any(m => !string.IsNullOrEmpty(m.TableType));
        if (tableTypeAvailable)
        {
            safeMeta = safeMeta.Where(em =>
                !string.Equals(em.TableType, "Virtual", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Fallback if TableType isn't populated in your SDK build:
            // Virtual tables expose ExternalCollectionName.
            safeMeta = safeMeta.Where(em => string.IsNullOrEmpty(em.ExternalCollectionName));
        }

        // Exclude intersection/link tables (N:N)
        safeMeta = safeMeta.Where(em => em.IsIntersect == false);

        // Favor user-facing tables (heuristic): can create forms OR views
        safeMeta = safeMeta.Where(em =>
            (em.CanCreateForms?.Value == true) || (em.CanCreateViews?.Value == true));

        var logicalNames = safeMeta
            .Select(em => em.LogicalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"Counting {logicalNames.Count} tables (non-virtual, non-intersection, user-facing)…");

        // 3) Retrieve counts in batches; fall back to per-table on batch failure
        const int batchSize = 150;
        var counted = new List<(string Table, long Count)>();
        var skipped = new List<(string Table, string Error)>();

        // Insert code here to create new list and call new code
        var nonEmptyTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < logicalNames.Count; i += batchSize)
        {
            var batch = logicalNames.Skip(i).Take(batchSize).ToArray();

            try
            {
                var batchResp =
                    (Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountResponse)org.Execute(
                        new Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountRequest
                        {
                            EntityNames = batch
                        });

                foreach (var kv in batchResp.EntityRecordCountCollection)
                {
                    counted.Add((kv.Key, kv.Value));
                    Console.WriteLine($"{kv.Key}\t{kv.Value}");
                }
            }
            catch (System.ServiceModel.FaultException ex)
            {
                // If batch fails, try per-table and skip those that still throw

                foreach (var table in batch)
                {
                    try
                    {
                        var singleResp =
                            (Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountResponse)org.Execute(
                                new Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountRequest
                                {
                                    EntityNames = new[] { table }
                                });

                        var item = singleResp.EntityRecordCountCollection.First();
                        counted.Add((item.Key, item.Value));
                        Console.WriteLine($"{item.Key}\t{item.Value}");

                        if (item.Value > 0)
                            nonEmptyTables.Add(item.Key);

                    }
                    catch (System.ServiceModel.FaultException singleEx)
                    {
                        skipped.Add((table, singleEx.Message));
                        Console.WriteLine($"SKIP {table}: {singleEx.Message}");
                    }
                }
            }
        }

        var wd = new WriteRowColumnsMetaData();
        wd.PrintColumnPopulationReport(org, nonEmptyTables);

        // 4) Write CSV outputs
        WriteCountsCsv(countsCsvPath, counted);
        WriteSkippedCsv(skippedCsvPath, skipped);

        Console.WriteLine($"\nDone. Counted: {counted.Count}, Skipped: {skipped.Count}");
        Console.WriteLine($"Counts CSV : {countsCsvPath}");
        Console.WriteLine($"Skipped CSV: {skippedCsvPath}");
    }

    // --- CSV helpers ---

    static void WriteCountsCsv(string path, List<(string Table, long Count)> rows)
    {
        EnsureDirectory(path);
        // Sort for stable output
        var ordered = rows.OrderBy(r => r.Table, StringComparer.OrdinalIgnoreCase).ToList();
        var sw = new StreamWriter(path, false); // overwrite
        sw.WriteLine("table,count");
        foreach (var r in ordered)
        {
            sw.WriteLine($"{Csv(r.Table)},{r.Count.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    static void WriteSkippedCsv(string path, List<(string Table, string Error)> rows)
    {
        EnsureDirectory(path);
        var ordered = rows.OrderBy(r => r.Table, StringComparer.OrdinalIgnoreCase).ToList();
        var sw = new StreamWriter(path, false); // overwrite
        sw.WriteLine("table,error");
        foreach (var r in ordered)
        {
            sw.WriteLine($"{Csv(r.Table)},{Csv(r.Error)}");
        }
    }

    static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    // Simple CSV escaping: wrap in quotes if needed, and escape embedded quotes
    static string Csv(string value)
    {
        if (value == null) return "";
        bool mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!mustQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }


    static void PrintNonVirtualTableCounts(IOrganizationService org)
    {
        // 1) Get table metadata (tables only)
        var metaReq = new RetrieveAllEntitiesRequest
        {
            EntityFilters = EntityFilters.Entity,   // only table-level info for performance
            RetrieveAsIfPublished = false
        };
        var metaResp = (RetrieveAllEntitiesResponse)org.Execute(metaReq); // returns EntityMetadata[]
                                                                          // Ref: RetrieveAllEntitiesRequest usage & notes.  [1](https://stackoverflow.com/questions/78250363/connecting-to-dataverse-api-in-c-sharp)

        // 2) Build a "safe" list:
        //    - exclude virtual tables
        //    - exclude M2M/intersection tables
        //    - prefer "user-facing" tables (CanCreateForms/CanCreateViews)
        IEnumerable<EntityMetadata> safeMeta = metaResp.EntityMetadata;

        // Exclude Virtual tables (preferred: TableType == "Virtual")
        // Some SDK builds may not expose TableType; in that case use ExternalCollectionName fallback.
        bool tableTypeAvailable = metaResp.EntityMetadata.Any(m => !string.IsNullOrEmpty(m.TableType));
        if (tableTypeAvailable)
        {
            safeMeta = safeMeta.Where(em => !string.Equals(em.TableType, "Virtual", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Fallback used/confirmed by community to detect virtual tables.
            safeMeta = safeMeta.Where(em => string.IsNullOrEmpty(em.ExternalCollectionName)); // Virtuals have this set
        }
        // Ref: Detecting/omitting virtual tables via TableType or ExternalCollectionName.  [2](https://stackoverflow.com/questions/77305273/how-can-i-implement-iorganizationservice-in-asp-net-core)

        // Exclude intersection/link tables
        safeMeta = safeMeta.Where(em => em.IsIntersect == false);

        // Keep "user-ish" tables that surface in the UI
        // (Heuristic: can create forms or views)
        safeMeta = safeMeta.Where(em => (em.CanCreateForms?.Value == true) || (em.CanCreateViews?.Value == true));

        // Optional: you can add additional excludes here (e.g., names ending with "usersettings", etc.)

        var logicalNames = safeMeta
            .Select(em => em.LogicalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"Counting {logicalNames.Count} tables (non-virtual, non-intersection, user-facing)…");

        // 3) Ask for snapshot counts (fast; snapshot within last 24 hours)
        //    Do it in batches and fall back to per-table on a failed batch so a single privilege
        //    issue won't stop the whole run.
        const int batchSize = 150;
        long totalOk = 0, totalSkipped = 0;

        for (int i = 0; i < logicalNames.Count; i += batchSize)
        {
            var batch = logicalNames.Skip(i).Take(batchSize).ToArray();

            try
            {
                var batchResp = (Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountResponse)org.Execute(
                    new Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountRequest
                    {
                        EntityNames = batch
                    });

                foreach (var kv in batchResp.EntityRecordCountCollection)
                {
                    Console.WriteLine($"{kv.Key}\t{kv.Value}");
                    totalOk++;
                }
            }
            catch (System.ServiceModel.FaultException ex)
            {
                // If the batch fails (e.g., one table is unreadable or unsupported),
                // try each table individually and skip those that still throw.
                foreach (var table in batch)
                {
                    try
                    {
                        var singleResp = (Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountResponse)org.Execute(
                            new Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountRequest
                            {
                                EntityNames = new[] { table }
                            });

                        var item = singleResp.EntityRecordCountCollection.First();
                        Console.WriteLine($"{item.Key}\t{item.Value}");
                        totalOk++;
                    }
                    catch (System.ServiceModel.FaultException singleEx)
                    {
                        Console.WriteLine($"SKIP {table}: {singleEx.Message}");
                        totalSkipped++;
                    }
                }
            }
        }

        Console.WriteLine($"\nDone. Counted: {totalOk}, Skipped: {totalSkipped}");
        // Note: counts come from a snapshot maintained by Dataverse (updated within last 24 hours).
        // Ref: RetrieveTotalRecordCount snapshot behavior.  [3](https://community.powerplatform.com/forums/thread/details/?threadid=88a5eca6-cdac-4677-b82d-fa43a24530c5)
    }

}


// .NET 6+ console app
// NuGet: Microsoft.PowerPlatform.Dataverse.Client

// org is your IOrganizationService (ServiceClient implements it)




static class TableCounts
{
    public static void Run(ServiceClient svc, bool writeCsv = false, string csvPath = null)
    {
        // 1) Retrieve all table metadata to get the logical names
        var metaReq = new RetrieveAllEntitiesRequest
        {
            EntityFilters = EntityFilters.Entity,   // only need table-level info
            RetrieveAsIfPublished = false
        };
        var metaResp = (RetrieveAllEntitiesResponse)svc.Execute(metaReq);

        // Filter out internal/link tables if you wish; here we include everything.
        var logicalNames = metaResp.EntityMetadata
            .Select(em => em.LogicalName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine($"Found {logicalNames.Length} tables. Getting snapshot counts...");

        // 2) Ask for counts. The request accepts multiple entity names.
        var countReq = new Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountRequest
        {
            EntityNames = logicalNames
        };

        var countResp = (Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountResponse)svc.Execute(countReq);

        // 3) Output to console and (optionally) CSV
        var items = countResp.EntityRecordCountCollection;
        Console.WriteLine();
        Console.WriteLine($"Table\tCount (snapshot in last 24h)");
        Console.WriteLine(new string('-', 40));

        var rows = new List<(string Table, long Count)>();
        foreach (var kvp in items)
        {
            Console.WriteLine($"{kvp.Key}\t{kvp.Value}");
            rows.Add((kvp.Key, kvp.Value));
        }

        if (writeCsv)
        {
            csvPath = Path.Combine(Directory.GetCurrentDirectory(), "dataverse_table_counts.csv");
            var w = new StreamWriter(csvPath);
            w.WriteLine("table,count");
            foreach (var r in rows)
                w.WriteLine($"{r.Table},{r.Count}");
            Console.WriteLine($"\nWrote CSV → {csvPath}");
        }
    }
}

