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
            if (line is null)
            {
                // Stdin closed (client disconnected)
                break;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Notifications do not have an "id"
                JsonElement? id = root.TryGetProperty("id", out var idProp) ? idProp : null;
                string method = root.TryGetProperty("method", out var methodProp) ? methodProp.GetString() ?? "" : "";
                JsonElement? @params = root.TryGetProperty("params", out var paramsProp) ? paramsProp : null;

                if (string.IsNullOrEmpty(method)) continue;

                // Handle notifications (no response needed)
                if (id is null)
                {
                    HandleNotification(method, @params);
                    continue;
                }

                // Handle requests (response required)
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
            "initialize" => HandleInitialize(id, @params),
            "ping" => HandlePing(id),
            "tools/list" => HandleToolsList(id),
            "tools/call" => HandleToolsCall(id, @params),
            _ => CreateErrorResponse(id, -32601, $"Method not found: {method}")
        };
    }

    private static JsonObject HandleInitialize(JsonElement id, JsonElement? @params)
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
                    ["tools"] = new JsonObject()
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "vacumify",
                    ["version"] = "1.0.0"
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
                ["description"] = "Scans directories using Vacumify's parallel multi-core engine. Identifies exact duplicates via SHA-256 fingerprinting, large storage hogs, and forgotten files in seconds.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Directory path to scan (e.g. workspace, project directory, C:\\Users\\...)"
                        },
                        ["includeHidden"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Whether to include hidden or dot folders (defaults to false)"
                        },
                        ["minDuplicateBytes"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "Minimum file size in bytes to check for duplicates (defaults to 102400 / 100 KB)"
                        }
                    },
                    ["required"] = new JsonArray { "path" }
                }
            },
            new JsonObject
            {
                ["name"] = "vacumify_get_dev_caches",
                ["description"] = "Finds dormant developer artifact folders (node_modules, Python venv/.venv, Rust target, .NET bin/obj, Gradle) with their project names and calculated sizes without traversing hundreds of thousands of subfiles.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Root project or workspace directory to scan for developer caches"
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
        string path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return $"Directory does not exist: '{path}'";
        }

        bool includeHidden = args.TryGetProperty("includeHidden", out var ih) && ih.GetBoolean();
        long minDupSize = args.TryGetProperty("minDuplicateBytes", out var ms) && ms.TryGetInt64(out var msv) ? msv : 100L * 1024;

        var options = new ScanOptions(
            IncludeHiddenCaches: includeHidden,
            MinDuplicateBytes: minDupSize,
            FastLargeFileSampling: true
        );

        var result = Task.Run(() => Scanner.ScanAsync([path], options, null, CancellationToken.None)).GetAwaiter().GetResult();

        var dupGroups = result.Files.Where(f => f.DuplicateGroupCount > 1).GroupBy(f => f.DuplicateGroupId).ToList();
        var heavyFiles = result.Files.OrderByDescending(f => f.Size).Take(10).ToList();

        var report = new JsonObject
        {
            ["scannedPath"] = path,
            ["totalFiles"] = result.Files.Count,
            ["totalScannedBytes"] = result.BytesScanned,
            ["totalScannedFormatted"] = FileEntry.FormatBytes(result.BytesScanned),
            ["duplicateGroupsCount"] = dupGroups.Count,
            ["reclaimableDuplicateBytes"] = result.ReclaimableDuplicateBytes,
            ["reclaimableDuplicateFormatted"] = FileEntry.FormatBytes(result.ReclaimableDuplicateBytes),
            ["devCachesCount"] = result.DevCaches.Count,
            ["devCachesTotalBytes"] = result.DevCaches.Sum(c => c.Size),
            ["devCachesTotalFormatted"] = FileEntry.FormatBytes(result.DevCaches.Sum(c => c.Size)),
            ["topHeavyFiles"] = new JsonArray(heavyFiles.Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name,
                ["size"] = f.SizeText,
                ["path"] = f.Path
            }).ToArray()),
            ["topDuplicateGroups"] = new JsonArray(dupGroups.Take(5).Select(g => (JsonNode)new JsonObject
            {
                ["groupId"] = g.Key,
                ["fileName"] = g.First().Name,
                ["copies"] = g.Count(),
                ["individualSize"] = g.First().SizeText,
                ["reclaimableBytes"] = (g.Count() - 1) * g.First().Size,
                ["paths"] = new JsonArray(g.Select(f => (JsonNode)f.Path).ToArray())
            }).ToArray())
        };

        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ExecuteGetDevCaches(JsonElement args)
    {
        string path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return $"Directory does not exist: '{path}'";
        }

        var options = new ScanOptions(
            IncludeHiddenCaches: true,
            MinDuplicateBytes: 100L * 1024,
            FastLargeFileSampling: true
        );

        var result = Task.Run(() => Scanner.ScanAsync([path], options, null, CancellationToken.None)).GetAwaiter().GetResult();

        var caches = result.DevCaches.Select(c => new JsonObject
        {
            ["project"] = c.ProjectName,
            ["cacheType"] = c.CacheType,
            ["path"] = c.Path,
            ["sizeBytes"] = c.Size,
            ["sizeFormatted"] = c.SizeText,
            ["lastModified"] = c.ModifiedText
        }).ToArray();

        var response = new JsonObject
        {
            ["totalCachesFound"] = caches.Length,
            ["totalReclaimableBytes"] = result.DevCaches.Sum(c => c.Size),
            ["totalReclaimableFormatted"] = FileEntry.FormatBytes(result.DevCaches.Sum(c => c.Size)),
            ["caches"] = new JsonArray(caches)
        };

        return response.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ExecuteGetSafeJunk(JsonElement args)
    {
        string path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
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
        if (!args.TryGetProperty("paths", out var pathsProp) || pathsProp.ValueKind != JsonValueKind.Array)
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
