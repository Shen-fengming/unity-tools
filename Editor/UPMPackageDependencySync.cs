#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShenFengming.UnityTools
{
    /// <summary>
    /// Sync package.json dependencies by analyzing asmdef references inside a target UPM package.
    /// - Primary source of truth: *.asmdef "references"
    /// - Resolves assembly -> package by scanning installed packages' asmdefs (Packages/ + Library/PackageCache/)
    /// - Versions are taken from current project's Packages/manifest.json when possible.
    /// </summary>
    public static class UPMPackageDependencySync
    {
        public sealed class SyncReport
        {
            public readonly HashSet<string> requiredAssemblies = new HashSet<string>();
            public readonly HashSet<string> resolvedPackages   = new HashSet<string>();

            public readonly List<string> addedDependencies     = new List<string>(); // "com.xxx@ver"
            public readonly List<string> updatedDependencies   = new List<string>(); // "com.xxx: old -> new"
            public readonly List<string> skippedAssemblies     = new List<string>(); // unresolved assemblies
            public readonly List<string> notes                 = new List<string>();

            public bool packageJsonChanged;
        }

        /// <summary>
        /// Public entry. Call this before saving/tagging.
        /// </summary>
        public static SyncReport SyncDependenciesFromAsmdef(string targetPackageRoot, bool fallbackScanCsUsings = true)
        {
            if (string.IsNullOrWhiteSpace(targetPackageRoot) || !Directory.Exists(targetPackageRoot))
                throw new Exception("Invalid target package root.");

            var packageJsonPath = Path.Combine(targetPackageRoot, "package.json");
            if (!File.Exists(packageJsonPath))
                throw new Exception("Target folder does not contain package.json.");

            // 1) Collect asmdef references inside target package
            var targetAsmdefRefs = CollectTargetAsmdefReferences(targetPackageRoot, out var hasAsmdef);
            var report = new SyncReport();
            foreach (var a in targetAsmdefRefs) report.requiredAssemblies.Add(a);

            // 2) Build assembly -> package map from installed packages
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var asmToPkg = BuildAssemblyToPackageMap(projectRoot);

            // 3) Resolve referenced assemblies to package ids
            var requiredPackages = new HashSet<string>();
            foreach (var asm in report.requiredAssemblies)
            {
                if (IsUnityBuiltinAssembly(asm))
                    continue;

                if (asmToPkg.TryGetValue(asm, out var pkgId) && !string.IsNullOrEmpty(pkgId))
                {
                    requiredPackages.Add(pkgId);
                }
                else
                {
                    // Some references could be local project assemblies or GUID refs
                    report.skippedAssemblies.Add(asm);
                }
            }

            // 4) Optional fallback: if target package has no asmdef, or to catch common cases
            if (fallbackScanCsUsings && (!hasAsmdef || report.requiredAssemblies.Count == 0))
            {
                var fallbackPkgs = DetectCommonPackagesFromCsUsing(targetPackageRoot);
                foreach (var p in fallbackPkgs) requiredPackages.Add(p);
                report.notes.Add(hasAsmdef
                    ? "Fallback scan enabled: added common dependencies inferred from C# usings."
                    : "No asmdef found: used fallback scan from C# usings for common dependencies.");
            }

            foreach (var p in requiredPackages) report.resolvedPackages.Add(p);

            // 5) Merge into target package.json dependencies using versions from project manifest
            var manifestDeps = ReadProjectManifestDependencies(projectRoot);
            report.packageJsonChanged = MergePackageJsonDependencies(packageJsonPath, requiredPackages, manifestDeps, report);

            return report;
        }

        // -------------------- Step 1: Collect target asmdef references --------------------

        private static HashSet<string> CollectTargetAsmdefReferences(string targetPackageRoot, out bool hasAsmdef)
        {
            var set = new HashSet<string>();

            // Prefer Runtime + Editor, but just scan all asmdefs inside package
            var asmdefs = Directory.GetFiles(targetPackageRoot, "*.asmdef", SearchOption.AllDirectories);
            hasAsmdef = asmdefs.Length > 0;

            foreach (var asm in asmdefs)
            {
                var json = File.ReadAllText(asm);

                // "references": [ "A", "B", ... ]
                var m = Regex.Match(json, "\"references\"\\s*:\\s*\\[(?<body>[\\s\\S]*?)\\]");
                if (!m.Success) continue;

                var body = m.Groups["body"].Value;
                var items = Regex.Matches(body, "\"(?<x>[^\"]+)\"");
                foreach (Match it in items)
                {
                    var r = it.Groups["x"].Value.Trim();

                    // Ignore GUID references (cannot resolve to package reliably here)
                    if (r.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase)) continue;

                    // Ignore empty
                    if (string.IsNullOrEmpty(r)) continue;

                    set.Add(r);
                }
            }
            return set;
        }

        // -------------------- Step 2: Build assembly -> package map --------------------

        /// <summary>
        /// Build map: asmdef "name" -> UPM package id, by scanning installed packages.
        /// Looks in:
        /// - {ProjectRoot}/Packages/*
        /// - {ProjectRoot}/Library/PackageCache/*
        /// </summary>
        private static Dictionary<string, string> BuildAssemblyToPackageMap(string projectRoot)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            // Packages (embedded / local)
            var packagesDir = Path.Combine(projectRoot, "Packages");
            ScanPackageDirectoryForAsmdefs(packagesDir, map);

            // PackageCache (downloaded)
            var cacheDir = Path.Combine(projectRoot, "Library", "PackageCache");
            ScanPackageDirectoryForAsmdefs(cacheDir, map);

            return map;
        }

        private static void ScanPackageDirectoryForAsmdefs(string root, Dictionary<string, string> asmToPkg)
        {
            if (!Directory.Exists(root)) return;

            // A "package folder" is one that contains package.json
            // but scanning every folder for package.json recursively is expensive.
            // We'll scan subdirectories that look like packages (one level deep is usually enough).
            var dirs = Directory.GetDirectories(root);
            foreach (var d in dirs)
            {
                var pj = Path.Combine(d, "package.json");
                if (!File.Exists(pj))
                {
                    // Some packages may nest; do a shallow check for safety
                    // (still cheap compared to full recursion)
                    continue;
                }

                var pkgId = ExtractJsonString(File.ReadAllText(pj), "name");
                if (string.IsNullOrEmpty(pkgId)) continue;

                var asmdefs = Directory.GetFiles(d, "*.asmdef", SearchOption.AllDirectories);
                foreach (var asmPath in asmdefs)
                {
                    var asmJson = File.ReadAllText(asmPath);
                    var asmName = ExtractJsonString(asmJson, "name");
                    if (string.IsNullOrEmpty(asmName)) continue;

                    // Don't overwrite if already mapped (prefer first found)
                    if (!asmToPkg.ContainsKey(asmName))
                        asmToPkg[asmName] = pkgId;
                }
            }
        }

        // -------------------- Step 3: manifest versions + merge package.json --------------------

        private static Dictionary<string, string> ReadProjectManifestDependencies(string projectRoot)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            if (!File.Exists(manifestPath)) return dict;

            var json = File.ReadAllText(manifestPath);

            var m = Regex.Match(json, "\"dependencies\"\\s*:\\s*\\{(?<body>[\\s\\S]*?)\\}");
            if (!m.Success) return dict;

            var body = m.Groups["body"].Value;
            var kv = Regex.Matches(body, "\"(?<k>[^\"]+)\"\\s*:\\s*\"(?<v>[^\"]+)\"");
            foreach (Match item in kv)
                dict[item.Groups["k"].Value] = item.Groups["v"].Value;

            return dict;
        }

        private static bool MergePackageJsonDependencies(
            string packageJsonPath,
            HashSet<string> requiredPackages,
            Dictionary<string, string> manifestVersions,
            SyncReport report)
        {
            if (requiredPackages == null || requiredPackages.Count == 0) return false;

            var json = File.ReadAllText(packageJsonPath);

            // Parse existing dependencies
            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            var depMatch = Regex.Match(json, "\"dependencies\"\\s*:\\s*\\{(?<body>[\\s\\S]*?)\\}");

            if (depMatch.Success)
            {
                var body = depMatch.Groups["body"].Value;
                var kv = Regex.Matches(body, "\"(?<k>[^\"]+)\"\\s*:\\s*\"(?<v>[^\"]+)\"");
                foreach (Match item in kv)
                    existing[item.Groups["k"].Value] = item.Groups["v"].Value;
            }

            bool changed = false;

            foreach (var pkg in requiredPackages)
            {
                var newVer = manifestVersions != null && manifestVersions.TryGetValue(pkg, out var mv)
                    ? mv
                    : DefaultVersion(pkg);

                if (existing.TryGetValue(pkg, out var oldVer))
                {
                    // Update only if we have a manifest version AND it's different
                    if (manifestVersions != null && manifestVersions.ContainsKey(pkg) && oldVer != newVer)
                    {
                        existing[pkg] = newVer;
                        report.updatedDependencies.Add($"{pkg}: {oldVer} -> {newVer}");
                        changed = true;
                    }
                }
                else
                {
                    existing[pkg] = newVer;
                    report.addedDependencies.Add($"{pkg}@{newVer}");
                    changed = true;
                }
            }

            if (!changed) return false;

            // Rebuild dependencies block (sorted)
            var keys = new List<string>(existing.Keys);
            keys.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append("{\n");
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                var v = existing[k];
                sb.Append($"    \"{EscapeJson(k)}\": \"{EscapeJson(v)}\"");
                sb.Append(i == keys.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  }");

            var newDepBlock = "\"dependencies\": " + sb.ToString();

            string updatedJson;
            if (depMatch.Success)
            {
                updatedJson = json.Substring(0, depMatch.Index)
                    + newDepBlock
                    + json.Substring(depMatch.Index + depMatch.Length);
            }
            else
            {
                // Insert before "author" if possible; else before last "}"
                var authorIdx = Regex.Match(json, "\"author\"\\s*:").Index;
                if (authorIdx > 0)
                {
                    var insert = "  " + newDepBlock + ",\n\n  ";
                    updatedJson = json.Insert(authorIdx, insert);
                }
                else
                {
                    // crude fallback: insert before the last }
                    var lastBrace = json.LastIndexOf('}');
                    if (lastBrace <= 0) throw new Exception("package.json malformed (cannot insert dependencies).");

                    // ensure there is a comma before insertion (best effort)
                    updatedJson = json.Insert(lastBrace, ",\n  " + newDepBlock + "\n");
                }
            }

            File.WriteAllText(packageJsonPath, updatedJson, new UTF8Encoding(false));
            return true;
        }

        // -------------------- Fallback: common using scan --------------------

        private static HashSet<string> DetectCommonPackagesFromCsUsing(string targetPackageRoot)
        {
            var pkgs = new HashSet<string>(StringComparer.Ordinal);
            var csFiles = Directory.GetFiles(targetPackageRoot, "*.cs", SearchOption.AllDirectories);

            foreach (var f in csFiles)
            {
                var t = File.ReadAllText(f);

                if (t.Contains("UnityEngine.InputSystem"))
                    pkgs.Add("com.unity.inputsystem");

                if (t.Contains("Cinemachine"))
                    pkgs.Add("com.unity.cinemachine");

                if (t.Contains("TMPro"))
                    pkgs.Add("com.unity.textmeshpro");

                if (t.Contains("UnityEngine.Rendering.Universal") || t.Contains("UniversalRenderPipeline"))
                    pkgs.Add("com.unity.render-pipelines.universal");
            }

            return pkgs;
        }

        // -------------------- Utility --------------------

        private static bool IsUnityBuiltinAssembly(string asmName)
        {
            // Most UnityEngine / UnityEditor modules are built-in, not UPM dependencies.
            // We do not add them to package.json dependencies.
            if (asmName.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
            if (asmName.StartsWith("UnityEditor", StringComparison.Ordinal)) return true;
            if (asmName.StartsWith("mscorlib", StringComparison.Ordinal)) return true;
            if (asmName.StartsWith("System", StringComparison.Ordinal)) return true;
            if (asmName.StartsWith("netstandard", StringComparison.Ordinal)) return true;
            return false;
        }

        private static string ExtractJsonString(string json, string key)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string DefaultVersion(string pkg)
        {
            // Only used if project manifest doesn't have it.
            return pkg switch
            {
                "com.unity.inputsystem" => "1.7.0",
                "com.unity.cinemachine" => "2.9.7",
                "com.unity.textmeshpro" => "3.0.9",
                "com.unity.render-pipelines.universal" => "17.0.0",
                _ => "1.0.0"
            };
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
#endif