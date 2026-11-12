using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualBasic.FileIO;

namespace Vacumify;

public static class McpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        Console.Error.WriteLine("[Vacumify MCP] Server started on stdio. Listening for JSON-RPC messages...");

        using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
        using var writer = new StreamWriter(Console.OpenStandardOutput(), System.Text.Encoding.UTF8) { AutoFlush = true };

        while (true)
        {
            string? line = reader.ReadLine();
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                JsonElement? id = root.TryGetProperty("id", out var idProp) ? idProp : null;
                string method = root.TryGetProperty("method", out var methodProp) ? methodProp.GetString() ?? "" : "";
                JsonElement? @params = root.TryGetProperty("params", out var paramsProp) ? paramsProp : null;

                if (string.IsNullOrEmpty(method)) continue;

                if (id is null)
                {
                    HandleNotification(method, @params);
                    continue;
                }

                var response = HandleRequest(id.Value, method, @params);
                if (response is not null)
                {
                    writer.WriteLine(response.ToJsonString(JsonOptions));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Vacumify MCP] Parse error: {ex.Message}");
            }
        }

        Console.Error.WriteLine("[Vacumify MCP] Server exiting.");
    }

    private static void HandleNotification(string method, JsonElement? @params)
    {
        if (method == "notifications/initialized")
        {
            Console.Error.WriteLine("[Vacumify MCP] Client initialized successfully.");
        }
    }

    private static JsonObject? HandleRequest(JsonElement id, string method, JsonElement? @params)
    {
        return method switch
        {
            "initialize" => HandleInitialize(id),
            "ping" => HandlePing(id),
            "tools/list" => HandleToolsList(id),
            "tools/call" => HandleToolsCall(id, @params),
            "prompts/list" => HandlePromptsList(id),
            "prompts/get" => HandlePromptsGet(id, @params),
            _ => CreateErrorResponse(id, -32601, $"Method not found: {method}")
        };
    }

    private static JsonObject HandleInitialize(JsonElement id)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject(),
                    ["prompts"] = new JsonObject()
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "vacumify",
                    ["version"] = "1.1.0"
                }
            }
        };
    }

    private static JsonObject HandlePing(JsonElement id)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = new JsonObject()
        };
    }

    private static JsonObject HandleToolsList(JsonElement id)
    {
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "vacumify_scan",
                ["description"] = "Scans directories using Vacumify's parallel multi-core engine. Identifies exact duplicates (SHA-256), large storage hogs, forgotten untouched files, and Active vs Abandoned project status.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Directory path to scan (e.g. workspace, C:\\Users\\...)"
                        },
                        ["forgottenDays"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "Days untouched threshold to classify a file as forgotten (e.g. 7, 15, 30, 60, 90; defaults to 30)"
                        },
                        ["minLargeFileBytes"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "Minimum file size in bytes to classify as a large file (e.g. 104857600 for 100MB, 524288000 for 500MB; defaults to 104857600)"
                        },
                        ["minDuplicateBytes"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "Minimum file size in bytes to check for duplicates (defaults to 102400 / 100 KB)"
                        },
                        ["includeHidden"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Whether to include hidden or dot folders (defaults to false)"
                        }
                    },
                    ["required"] = new JsonArray { "path" }
                }
            },
            new JsonObject
            {
                ["name"] = "vacumify_get_dev_caches",
                ["description"] = "Finds dormant developer artifact folders (node_modules, Python venv/.venv, Rust target, .NET bin/obj, Gradle) with sizes, age filter, and Active vs Abandoned project scoring.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Root project or workspace directory to scan for developer caches"
                        },
                        ["olderThanDays"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "Optional minimum days since last modified (e.g. 30, 60). Defaults to 0 (all caches)."
                        }
                    },
                    ["required"] = new JsonArray { "path" }
                }
            },
            new JsonObject
            {
                ["name"] = "vacumify_get_safe_junk",
                ["description"] = "Identifies 100% zero-risk safe junk files: redundant duplicate copies (preserving copy #1 intact), obsolete installers in Downloads untouched for 30+ days, and dead crash dumps.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Directory path to analyze for safe junk"
                        }
                    },
                    ["required"] = new JsonArray { "path" }
                }
            },
            new JsonObject
            {
                ["name"] = "vacumify_recycle_items",
                ["description"] = "Safely moves specified files or folders to the Windows Recycle Bin (restorable, never permanently wiped). System protected paths are guarded.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["paths"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "Array of absolute file or directory paths to send to the Windows Recycle Bin"
                        },
                        ["dryRun"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "If true, simulates the action without moving anything (defaults to false)"
                        }
                    },
                    ["required"] = new JsonArray { "paths" }
                }
            }
        };

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = new JsonObject
            {
                ["tools"] = tools
            }
        };
    }

    private static JsonObject HandlePromptsList(JsonElement id)
    {
        var prompts = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "audit-storage",
                ["description"] = "Full disk audit across duplicates, large files, forgotten files, and dormant dev caches with Active vs Abandoned project scoring.",
                ["arguments"] = new JsonArray
                {
                    new JsonObject { ["name"] = "path", ["description"] = "Target directory path (defaults to current workspace)", ["required"] = false },
                    new JsonObject { ["name"] = "forgottenDays", ["description"] = "Untouched days threshold (default: 30)", ["required"] = false },
                    new JsonObject { ["name"] = "minLargeFileMB", ["description"] = "Large file threshold in MB (default: 100)", ["required"] = false },
                    new JsonObject { ["name"] = "minDuplicateKB", ["description"] = "Minimum duplicate size in KB (default: 100)", ["required"] = false }
                }
            },
            new JsonObject
            {
                ["name"] = "duplicates",
                ["description"] = "Find identical duplicate files with Keep Newest recommendation and Active/Abandoned project awareness.",
                ["arguments"] = new JsonArray
                {
                    new JsonObject { ["name"] = "path", ["description"] = "Target directory path", ["required"] = false },
                    new JsonObject { ["name"] = "minDuplicateKB", ["description"] = "Minimum duplicate size in KB (default: 100)", ["required"] = false }
                }
            },
            new JsonObject
            {
                ["name"] = "forgotten-files",
                ["description"] = "Find files untouched for N days (7, 15, 30, 60, 90+) with project activity status.",
                ["arguments"] = new JsonArray
                {
                    new JsonObject { ["name"] = "path", ["description"] = "Target directory path", ["required"] = false },
                    new JsonObject { ["name"] = "forgottenDays", ["description"] = "Days untouched threshold (default: 30)", ["required"] = false }
                }
            },
            new JsonObject
            {
                ["name"] = "large-files",
                ["description"] = "Find heavy storage hogs (100 MB+, 500 MB+, 1 GB+) and check if they live in active or abandoned projects.",
                ["arguments"] = new JsonArray
                {
                    new JsonObject { ["name"] = "path", ["description"] = "Target directory path", ["required"] = false },
                    new JsonObject { ["name"] = "minLargeFileMB", ["description"] = "Minimum size in MB (default: 100)", ["required"] = false }
                }
            },
            new JsonObject
            {
                ["name"] = "dev-caches",
                ["description"] = "Hunt dormant node_modules, Python venv, Rust target, and .NET bin/obj folders while protecting active projects.",
                ["arguments"] = new JsonArray
                {
                    new JsonObject { ["name"] = "path", ["description"] = "Root projects folder path", ["required"] = false },
                    new JsonObject { ["name"] = "olderThanDays", ["description"] = "Only show caches untouched for at least N days (default: 30)", ["required"] = false }
                }
            }
        };

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = new JsonObject
            {
                ["prompts"] = prompts
            }
        };
    }

    private static JsonObject HandlePromptsGet(JsonElement id, JsonElement? @params)
    {
        if (@params is null || !@params.Value.TryGetProperty("name", out var nameProp))
        {
            return CreateErrorResponse(id, -32602, "Invalid params: 'name' is required");
        }

        string name = nameProp.GetString() ?? "";
        JsonElement args = @params.Value.TryGetProperty("arguments", out var argsProp) ? argsProp : default;

        string GetArg(string key, string fallback) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!
                : fallback;

        string path = GetArg("path", ".");
        string forgottenDays = GetArg("forgottenDays", "30");
        string minLargeMB = GetArg("minLargeFileMB", "100");
        string minDupKB = GetArg("minDuplicateKB", "100");
        string olderThanDays = GetArg("olderThanDays", "30");

        string? instruction = name switch
        {
            "audit-storage" =>
                $"Run a full Vacumify storage audit on '{path}' by calling `vacumify_scan` (with forgottenDays={forgottenDays}, minLargeFileBytes={minLargeMB}*1024*1024, minDuplicateBytes={minDupKB}*1024) and `vacumify_get_dev_caches`. " +
                "Present a clear breakdown of Reclaimable Duplicates, Large Files, Forgotten Files, and Dormant Dev Caches. " +
                "Prioritize items in ABANDONED projects and warn/protect items in ACTIVE projects. " +
                "Ask for user confirmation before calling `vacumify_recycle_items` (which strictly sends items to the Windows Recycle Bin, never permanent delete).",

            "duplicates" =>
                $"Call `vacumify_scan` on '{path}' with minDuplicateBytes={minDupKB}*1024. " +
                "List duplicate file groups, identify how much space can be reclaimed by keeping the newest copy, and check whether copies are in ACTIVE or ABANDONED projects. " +
                "Propose a Recycle Bin cleanup plan using `vacumify_recycle_items` with dryRun=true first, and wait for user approval before moving items to the Windows Recycle Bin.",

            "forgotten-files" =>
                $"Call `vacumify_scan` on '{path}' with forgottenDays={forgottenDays}. " +
                $"Highlight files untouched for {forgottenDays}+ days, noting their activityStatus (ACTIVE vs DORMANT vs ABANDONED). " +
                "Wait for user approval before sending any selected files to the Windows Recycle Bin via `vacumify_recycle_items`.",

            "large-files" =>
                $"Call `vacumify_scan` on '{path}' with minLargeFileBytes={minLargeMB}*1024*1024. " +
                $"Rank the heaviest files >= {minLargeMB} MB, showing their size and whether they reside in ACTIVE or ABANDONED projects. " +
                "Never delete automatically; present options and move approved files to the Windows Recycle Bin via `vacumify_recycle_items`.",

            "dev-caches" =>
                $"Call `vacumify_get_dev_caches` on '{path}' with olderThanDays={olderThanDays}. " +
                "Group results by activityStatus: mark ACTIVE projects (last touched <= 14 days) as protected/skip, and highlight DORMANT/ABANDONED project caches (node_modules, venv, target, bin/obj) as safe cleanup targets. " +
                "Confirm with the user before recycling via `vacumify_recycle_items`.",

            _ => null
        };

        if (instruction is null)
        {
            return CreateErrorResponse(id, -32602, $"Unknown prompt: {name}");
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = new JsonObject
            {
                ["description"] = $"Vacumify workflow for {name}",
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = instruction
                        }
                    }
                }
            }
        };
    }

    private static JsonObject HandleToolsCall(JsonElement id, JsonElement? @params)
    {
        if (@params is null || !@params.Value.TryGetProperty("name", out var nameProp))
        {
            return CreateErrorResponse(id, -32602, "Invalid params: 'name' is required");
        }

        string toolName = nameProp.GetString() ?? "";
        JsonElement arguments = @params.Value.TryGetProperty("arguments", out var argsProp) ? argsProp : default;

        try
        {
            var content = toolName switch
            {
                "vacumify_scan" => ExecuteScan(arguments),
                "vacumify_get_dev_caches" => ExecuteGetDevCaches(arguments),
                "vacumify_get_safe_junk" => ExecuteGetSafeJunk(arguments),
                "vacumify_recycle_items" => ExecuteRecycleItems(arguments),
                _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
            };

            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["result"] = new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = content
                        }
                    },
                    ["isError"] = false
                }
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["result"] = new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"Error executing {toolName}: {ex.Message}"
                        }
                    },
                    ["isError"] = true
                }
            };
        }
    }

    private static string ExecuteScan(JsonElement args)
    {
        string path = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return $"Directory does not exist: '{path}'";
        }

        int forgottenDays = args.TryGetProperty("forgottenDays", out var fd) && fd.TryGetInt32(out var fdv) ? Math.Max(1, fdv) : 30;
        long minLargeBytes = args.TryGetProperty("minLargeFileBytes", out var mlb) && mlb.TryGetInt64(out var mlbv) ? Math.Max(0, mlbv) : 100L * 1024 * 1024;
        long minDupSize = args.TryGetProperty("minDuplicateBytes", out var ms) && ms.TryGetInt64(out var msv) ? Math.Max(1, msv) : 100L * 1024;
        bool includeHidden = args.TryGetProperty("includeHidden", out var ih) && ih.GetBoolean();

        var options = new ScanOptions(
            IncludeHiddenCaches: includeHidden,
            MinDuplicateBytes: minDupSize,
            FastLargeFileSampling: true
        );

        var result = Task.Run(() => Scanner.ScanAsync([path], options, null, CancellationToken.None)).GetAwaiter().GetResult();

        DateTime forgottenCutoff = DateTime.UtcNow.AddDays(-forgottenDays);
        var forgottenFiles = result.Files.Where(f => f.LastAccessUtc <= forgottenCutoff).OrderBy(f => f.LastAccessUtc).ToList();
        var largeFiles = result.Files.Where(f => f.Size >= minLargeBytes).OrderByDescending(f => f.Size).ToList();
        var dupGroups = result.Files.Where(f => f.DuplicateGroupCount > 1).GroupBy(f => f.DuplicateGroupId).ToList();

        var report = new JsonObject
        {
            ["scannedPath"] = path,
            ["conditions"] = new JsonObject
            {
                ["forgottenDays"] = forgottenDays,
                ["minLargeFileFormatted"] = FileEntry.FormatBytes(minLargeBytes),
                ["minDuplicateFormatted"] = FileEntry.FormatBytes(minDupSize),
                ["includeHidden"] = includeHidden
            },
            ["totalFiles"] = result.Files.Count,
            ["totalScannedBytes"] = result.BytesScanned,
            ["totalScannedFormatted"] = FileEntry.FormatBytes(result.BytesScanned),
            ["duplicateGroupsCount"] = dupGroups.Count,
            ["reclaimableDuplicateBytes"] = result.ReclaimableDuplicateBytes,
            ["reclaimableDuplicateFormatted"] = FileEntry.FormatBytes(result.ReclaimableDuplicateBytes),
            ["largeFilesCount"] = largeFiles.Count,
            ["largeFilesTotalFormatted"] = FileEntry.FormatBytes(largeFiles.Sum(f => f.Size)),
            ["forgottenFilesCount"] = forgottenFiles.Count,
            ["forgottenFilesTotalFormatted"] = FileEntry.FormatBytes(forgottenFiles.Sum(f => f.Size)),
            ["devCachesCount"] = result.DevCaches.Count,
            ["devCachesTotalFormatted"] = FileEntry.FormatBytes(result.DevCaches.Sum(c => c.Size)),
            ["topLargeFiles"] = new JsonArray(largeFiles.Take(10).Select(f =>
            {
                var activity = Scanner.GetProjectActivity(f.Path, f.LastWriteUtc);
                return (JsonNode)new JsonObject
                {
                    ["name"] = f.Name,
                    ["size"] = f.SizeText,
                    ["path"] = f.Path,
                    ["activityStatus"] = activity.Status,
                    ["daysUntouched"] = activity.DaysUntouched
                };
            }).ToArray()),
            ["topForgottenFiles"] = new JsonArray(forgottenFiles.Take(10).Select(f =>
            {
                var activity = Scanner.GetProjectActivity(f.Path, f.LastWriteUtc);
                return (JsonNode)new JsonObject
                {
                    ["name"] = f.Name,
                    ["size"] = f.SizeText,
                    ["lastAccessed"] = f.AccessedText,
                    ["path"] = f.Path,
                    ["activityStatus"] = activity.Status,
                    ["daysUntouched"] = activity.DaysUntouched
                };
            }).ToArray()),
            ["topDuplicateGroups"] = new JsonArray(dupGroups.Take(8).Select(g =>
            {
                var copies = g.OrderByDescending(f => f.LastWriteUtc).ToList();
                return (JsonNode)new JsonObject
                {
                    ["groupId"] = g.Key,
                    ["fileName"] = copies[0].Name,
                    ["copiesCount"] = copies.Count,
                    ["individualSize"] = copies[0].SizeText,
                    ["reclaimableBytes"] = (copies.Count - 1) * copies[0].Size,
                    ["copies"] = new JsonArray(copies.Select(c =>
                    {
                        var activity = Scanner.GetProjectActivity(c.Path, c.LastWriteUtc);
                        return (JsonNode)new JsonObject
                        {
                            ["path"] = c.Path,
                            ["modified"] = c.ModifiedText,
                            ["activityStatus"] = activity.Status
                        };
                    }).ToArray())
                };
            }).ToArray())
        };

        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ExecuteGetDevCaches(JsonElement args)
    {
        string path = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return $"Directory does not exist: '{path}'";
        }

        int olderThanDays = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("olderThanDays", out var otd) && otd.TryGetInt32(out var otdv) ? Math.Max(0, otdv) : 0;

        var options = new ScanOptions(
            IncludeHiddenCaches: true,
            MinDuplicateBytes: 100L * 1024,
            FastLargeFileSampling: true
        );

        var result = Task.Run(() => Scanner.ScanAsync([path], options, null, CancellationToken.None)).GetAwaiter().GetResult();

        var filtered = result.DevCaches.AsEnumerable();
        if (olderThanDays > 0)
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
            filtered = filtered.Where(c => c.LastModifiedUtc <= cutoff);
        }

        var cacheList = filtered.ToList();
        var cachesJson = cacheList.Select(c =>
        {
            var activity = Scanner.GetProjectActivity(c.Path, c.LastModifiedUtc);
            return new JsonObject
            {
                ["project"] = c.ProjectName,
                ["cacheType"] = c.CacheType,
                ["path"] = c.Path,
                ["sizeBytes"] = c.Size,
                ["sizeFormatted"] = c.SizeText,
                ["lastModified"] = c.ModifiedText,
                ["activityStatus"] = activity.Status,
                ["daysUntouched"] = activity.DaysUntouched
            };
        }).ToArray();

        var response = new JsonObject
        {
            ["olderThanDaysFilter"] = olderThanDays,
            ["totalCachesFound"] = cachesJson.Length,
            ["totalReclaimableBytes"] = cacheList.Sum(c => c.Size),
            ["totalReclaimableFormatted"] = FileEntry.FormatBytes(cacheList.Sum(c => c.Size)),
            ["caches"] = new JsonArray(cachesJson)
        };

        return response.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ExecuteGetSafeJunk(JsonElement args)
    {
        string path = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return $"Directory does not exist: '{path}'";
        }

        var options = new ScanOptions(
            IncludeHiddenCaches: false,
            MinDuplicateBytes: 100L * 1024,
            FastLargeFileSampling: true
        );

        var result = Task.Run(() => Scanner.ScanAsync([path], options, null, CancellationToken.None)).GetAwaiter().GetResult();
        var safeJunk = Scanner.IdentifySafeJunk(result.Files);

        var dupFiles = safeJunk.Where(f => f.DuplicateGroupCount > 1).ToList();
        var installerFiles = safeJunk.Where(f => f.DuplicateGroupCount <= 1 && (f.ExtensionText is "MSI" or "EXE")).ToList();
        var dumpFiles = safeJunk.Where(f => f.DuplicateGroupCount <= 1 && (f.ExtensionText is "DMP" or "LOG")).ToList();

        var response = new JsonObject
        {
            ["safeJunkFilesCount"] = safeJunk.Count,
            ["totalReclaimableBytes"] = safeJunk.Sum(f => f.Size),
            ["totalReclaimableFormatted"] = FileEntry.FormatBytes(safeJunk.Sum(f => f.Size)),
            ["redundantDuplicates"] = new JsonObject
            {
                ["count"] = dupFiles.Count,
                ["sizeFormatted"] = FileEntry.FormatBytes(dupFiles.Sum(f => f.Size)),
                ["items"] = new JsonArray(dupFiles.Take(30).Select(f => (JsonNode)new JsonObject
                {
                    ["name"] = f.Name,
                    ["size"] = f.SizeText,
                    ["path"] = f.Path
                }).ToArray())
            },
            ["obsoleteInstallers"] = new JsonObject
            {
                ["count"] = installerFiles.Count,
                ["sizeFormatted"] = FileEntry.FormatBytes(installerFiles.Sum(f => f.Size)),
                ["items"] = new JsonArray(installerFiles.Select(f => (JsonNode)new JsonObject
                {
                    ["name"] = f.Name,
                    ["size"] = f.SizeText,
                    ["path"] = f.Path
                }).ToArray())
            },
            ["crashDumpsAndLogs"] = new JsonObject
            {
                ["count"] = dumpFiles.Count,
                ["sizeFormatted"] = FileEntry.FormatBytes(dumpFiles.Sum(f => f.Size)),
                ["items"] = new JsonArray(dumpFiles.Select(f => (JsonNode)new JsonObject
                {
                    ["name"] = f.Name,
                    ["size"] = f.SizeText,
                    ["path"] = f.Path
                }).ToArray())
            }
        };

        return response.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ExecuteRecycleItems(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("paths", out var pathsProp) || pathsProp.ValueKind != JsonValueKind.Array)
        {
            return "Missing required parameter 'paths' (must be an array of strings)";
        }

        bool dryRun = args.TryGetProperty("dryRun", out var dr) && dr.GetBoolean();
        var paths = pathsProp.EnumerateArray().Select(p => p.GetString()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();

        int recycled = 0, failed = 0, protectedSkipped = 0;
        long recycledBytes = 0;
        var recycledList = new List<string>();

        foreach (var p in paths)
        {
            if (Scanner.IsProtectedPath(p) || Scanner.IsProtectedRoot(p))
            {
                protectedSkipped++;
                continue;
            }

            if (File.Exists(p))
            {
                try
                {
                    var fi = new FileInfo(p);
                    long sz = fi.Length;
                    if (!dryRun)
                    {
                        FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    recycled++;
                    recycledBytes += sz;
                    recycledList.Add(p);
                }
                catch
                {
                    failed++;
                }
            }
            else if (Directory.Exists(p))
            {
                try
                {
                    long sz = Scanner.FastGetDirectorySize(p);
                    if (!dryRun)
                    {
                        FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    recycled++;
                    recycledBytes += sz;
                    recycledList.Add(p);
                }
                catch
                {
                    failed++;
                }
            }
            else
            {
                failed++;
            }
        }

        var res = new JsonObject
        {
            ["dryRun"] = dryRun,
            ["recycledCount"] = recycled,
            ["recycledBytes"] = recycledBytes,
            ["recycledFormatted"] = FileEntry.FormatBytes(recycledBytes),
            ["failedCount"] = failed,
            ["protectedSkipped"] = protectedSkipped,
            ["recycledPaths"] = new JsonArray(recycledList.Take(50).Select(p => (JsonNode)p).ToArray())
        };

        return res.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject CreateErrorResponse(JsonElement id, int code, string message)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
    }
}
