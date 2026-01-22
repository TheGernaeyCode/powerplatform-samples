using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.IO;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Messages;
using System.Security;

namespace DataverseTableRowCounts
{
    public class WriteRowColumnsMetaData
    {

        public void PrintColumnPopulationReport(
            IOrganizationService org,
            IEnumerable<string> tableLogicalNames,
            string columnsCsvPath = null,
            bool onlyCustomAttributes = false,
            bool skipSystemAttributes = true,
            int? maxTables = null,
            int? maxAttributesPerTable = null)
        {
            columnsCsvPath = Path.Combine(@"D:\TableCounts\", "dataverse_column_population.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(columnsCsvPath));

            var rows = new List<ColumnPopulationRow>();

            // Optional capping for very large environments
            var tables = (maxTables.HasValue ? tableLogicalNames.Take(maxTables.Value) : tableLogicalNames).ToList();
            Console.WriteLine($"Analyzing column population across {tables.Count} tables...");

            int tableIndex = 0;
            foreach (var entityLogicalName in tables)
            {
                tableIndex++;
                try
                {
                    // Pull entity + attribute metadata
                    var md = RetrieveEntityWithAttributes(org, entityLogicalName);
                    if (md == null)
                    {
                        Console.WriteLine($"SKIP {entityLogicalName}: failed to retrieve metadata.");
                        continue;
                    }

                    var tableDisplayName = md.DisplayName?.UserLocalizedLabel?.Label ?? entityLogicalName;
                    var primaryId = md.PrimaryIdAttribute; // e.g., accountid

                    // Check if createdon/modifiedon exist on this table (almost all do)
                    bool hasCreatedOn = md.Attributes.Any(a => a.LogicalName == "createdon");
                    bool hasModifiedOn = md.Attributes.Any(a => a.LogicalName == "modifiedon");

                    // Select attributes to evaluate
                    IEnumerable<AttributeMetadata> attributes = md.Attributes;

                    if (onlyCustomAttributes)
                        attributes = attributes.Where(a => a.IsCustomAttribute == true);

                    if (skipSystemAttributes)
                        attributes = attributes.Where(a =>
                            a.IsValidForRead == true &&
                            a.IsPrimaryId != true &&
                            a.LogicalName != "createdon" &&
                            a.LogicalName != "modifiedon" &&
                            a.LogicalName != "overriddencreatedon" &&
                            a.LogicalName != "versionnumber" &&
                            a.LogicalName != "timezoneruleversionnumber" &&
                            a.LogicalName != "importsequencenumber");

                    // Optionally cap
                    if (maxAttributesPerTable.HasValue)
                        attributes = attributes.Take(maxAttributesPerTable.Value);

                    var attrs = attributes
                        .OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    Console.WriteLine($"[{tableIndex}/{tables.Count}] {entityLogicalName}: evaluating {attrs.Count} columns...");

                    foreach (var attr in attrs)
                    {
                        var attrLogical = attr.LogicalName;
                        var attrDisplay = attr.DisplayName?.UserLocalizedLabel?.Label ?? attrLogical;
                        var typeName = attr.AttributeTypeName?.Value ?? attr.AttributeType?.ToString() ?? "unknown";

                        // Some attribute types or virtual fields can be noisy; you can skip more types here if needed.
                        if (!IsQueryableForNotNullCheck(attr))
                        {
                            rows.Add(new ColumnPopulationRow
                            {
                                TableLogicalName = entityLogicalName,
                                TableDisplayName = tableDisplayName,
                                ColumnLogicalName = attrLogical,
                                ColumnDisplayName = attrDisplay,
                                AttributeType = typeName,
                                HasData = null,
                                NonNullCount = null,
                                FirstSeenUtc = null,
                                LastSeenUtc = null,
                                Notes = "Skipped: non-queryable/virtual/file/image/calculated/rollup"
                            });
                            continue;
                        }

                        try
                        {
                            var stats = GetColumnStatsAggregate(org, entityLogicalName, attrLogical, primaryId, hasCreatedOn, hasModifiedOn);

                            rows.Add(new ColumnPopulationRow
                            {
                                TableLogicalName = entityLogicalName,
                                TableDisplayName = tableDisplayName,
                                ColumnLogicalName = attrLogical,
                                ColumnDisplayName = attrDisplay,
                                AttributeType = typeName,
                                HasData = stats.HasData,
                                NonNullCount = stats.Count,
                                FirstSeenUtc = stats.First,
                                LastSeenUtc = stats.Last,
                                Notes = stats.Notes
                            });
                        }
/*                        catch (OrganizationServiceFaultException osf)
                        {
                            rows.Add(new ColumnPopulationRow
                            {
                                TableLogicalName = entityLogicalName,
                                TableDisplayName = tableDisplayName,
                                ColumnLogicalName = attrLogical,
                                ColumnDisplayName = attrDisplay,
                                AttributeType = typeName,
                                HasData = null,
                                NonNullCount = null,
                                FirstSeenUtc = null,
                                LastSeenUtc = null,
                                Notes = $"Error: {osf.Message}"
                            });
                        }*/
                        catch (Exception ex)
                        {
                            rows.Add(new ColumnPopulationRow
                            {
                                TableLogicalName = entityLogicalName,
                                TableDisplayName = tableDisplayName,
                                ColumnLogicalName = attrLogical,
                                ColumnDisplayName = attrDisplay,
                                AttributeType = typeName,
                                HasData = null,
                                NonNullCount = null,
                                FirstSeenUtc = null,
                                LastSeenUtc = null,
                                Notes = $"Error: {ex.Message}"
                            });
                        }
                    }
                }
                catch (Exception exTable)
                {
                    Console.WriteLine($"Error on {entityLogicalName}: {exTable.Message}");
                }
            }

            WriteColumnPopulationCsv(columnsCsvPath, rows);
            Console.WriteLine($"\nDone. Column population CSV: {columnsCsvPath}");
        }


        public EntityMetadata RetrieveEntityWithAttributes(IOrganizationService org, string entityLogicalName)
        {
            var req = new RetrieveEntityRequest
            {
                LogicalName = entityLogicalName,
                EntityFilters = EntityFilters.Entity | EntityFilters.Attributes,
                RetrieveAsIfPublished = false
            };
            var resp = (RetrieveEntityResponse)org.Execute(req);
            return resp?.EntityMetadata;
        }



        public bool IsQueryableForNotNullCheck(AttributeMetadata attr)
        {
            if (attr == null) return false;

            // Not readable? Skip.
            if (attr.IsValidForRead != true) return false;

            // Skip calculated or rollup attributes (virtual / computed)
            var typeName = attr.AttributeTypeName?.Value ?? attr.AttributeType?.ToString() ?? string.Empty;
            if (typeName.IndexOf("Calculated", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (typeName.IndexOf("Rollup", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            // Skip file and image columns (aggregate/not-null checks can be odd or unsupported)
            if (string.Equals(typeName, "FileType", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(typeName, "ImageType", StringComparison.OrdinalIgnoreCase)) return false;

            // Optional: Skip partylist, activityparty, and other noisy/complex types
            if (string.Equals(typeName, "PartyListType", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(typeName, "EntityNameType", StringComparison.OrdinalIgnoreCase)) return false;

            // Optional: Skip large memo fields if you’ve seen aggregate quirks (usually fine to include)
            // if (string.Equals(typeName, "MemoType", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }



        public class ColumnStats
        {
            public bool HasData { get; set; }
            public long? Count { get; set; }
            public DateTime? First { get; set; }
            public DateTime? Last { get; set; }
            public string Notes { get; set; } = "";
        }

        public ColumnStats GetColumnStatsAggregate(
            IOrganizationService org,
            string entityLogicalName,
            string attributeLogicalName,
            string primaryIdLogicalName,
            bool hasCreatedOn,
            bool hasModifiedOn)
        {
            // Build aggregate fetch:
            // - count of rows with attribute not null
            // - min(createdon) among those rows
            // - max(modifiedon) among those rows
            // Some system tables may lack createdon/modifiedon—guard with flags.
            var countAgg = $"<attribute name='{primaryIdLogicalName}' alias='cnt' aggregate='count' />";
            var minAgg = hasCreatedOn ? "<attribute name='createdon' alias='first' aggregate='min' />" : null;
            var maxAgg = hasModifiedOn ? "<attribute name='modifiedon' alias='last' aggregate='max' />" : null;

            string aggs = string.Join("", new[] { countAgg, minAgg, maxAgg }.Where(s => !string.IsNullOrEmpty(s)));

            string fetch = $@"
                <fetch aggregate='true'>
                  <entity name='{SecurityElement.Escape(entityLogicalName)}'>
                    {aggs}
                    <filter>
                      <condition attribute='{SecurityElement.Escape(attributeLogicalName)}' operator='not-null' />
                    </filter>
                  </entity>
                </fetch>";

            var resp = org.RetrieveMultiple(new FetchExpression(fetch));

            if (resp.Entities.Count == 0)
            {
                return new ColumnStats
                {
                    HasData = false,
                    Count = 0,
                    First = null,
                    Last = null,
                    Notes = ""
                };
            }

            var row = resp.Entities[0];
            var stats = new ColumnStats
            {
                HasData = true,
                Count = TryGetAliasedLong(row, "cnt"),
                First = hasCreatedOn ? TryGetAliasedDateTime(row, "first") : null,
                Last = hasModifiedOn ? TryGetAliasedDateTime(row, "last") : null
            };

            // Notes for clarity when createdon/modifiedon missing
            if (!hasCreatedOn || !hasModifiedOn)
            {
                stats.Notes = $"first/last limited (createdon:{hasCreatedOn}, modifiedon:{hasModifiedOn})";
            }

            return stats;
        }

        public long? TryGetAliasedLong(Entity e, string alias)
        {
            if (!e.Attributes.TryGetValue(alias, out var val)) return null;
            if (val is AliasedValue av)
            {
                if (av.Value == null) return null;
                try { return Convert.ToInt64(av.Value, CultureInfo.InvariantCulture); } catch { return null; }
            }
            try { return Convert.ToInt64(val, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public DateTime? TryGetAliasedDateTime(Entity e, string alias)
        {
            if (!e.Attributes.TryGetValue(alias, out var val)) return null;
            if (val is AliasedValue av)
            {
                if (av.Value == null) return null;
                if (av.Value is DateTime dt) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
            if (val is DateTime d) return DateTime.SpecifyKind(d, DateTimeKind.Utc);
            return null;
        }


        public class ColumnPopulationRow
        {
            public string TableLogicalName { get; set; }
            public string TableDisplayName { get; set; }
            public string ColumnLogicalName { get; set; }
            public string ColumnDisplayName { get; set; }
            public string AttributeType { get; set; }
            public bool? HasData { get; set; }
            public long? NonNullCount { get; set; }
            public DateTime? FirstSeenUtc { get; set; }
            public DateTime? LastSeenUtc { get; set; }
            public string Notes { get; set; }
        }

        public void WriteColumnPopulationCsv(string path, IEnumerable<ColumnPopulationRow> rows)
        {
            var sw = new StreamWriter(path, false);
            sw.WriteLine("table_logical_name,table_display_name,column_logical_name,column_display_name,attribute_type,has_data,nonnull_count,first_seen_utc,last_seen_utc,notes");

            foreach (var r in rows)
            {
                string Dt(DateTime? d) => d.HasValue ? d.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : "";
                string Esc(string s) => s == null ? "" : s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

                sw.WriteLine(string.Join(",",
                    Esc(r.TableLogicalName),
                    Esc(r.TableDisplayName),
                    Esc(r.ColumnLogicalName),
                    Esc(r.ColumnDisplayName),
                    Esc(r.AttributeType),
                    r.HasData.HasValue ? r.HasData.Value.ToString().ToLowerInvariant() : "",
                    r.NonNullCount?.ToString(CultureInfo.InvariantCulture) ?? "",
                    Esc(Dt(r.FirstSeenUtc)),
                    Esc(Dt(r.LastSeenUtc)),
                    Esc(r.Notes)
                ));
            }
        }


    }
}




