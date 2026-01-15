#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace ShenFengming.UnityTools
{
    public sealed class PackageDependencyChainScannerWindow : EditorWindow
    {
        private string packageRootAbs = "";      // absolute path chosen by user
        private string packageRootUnity = "";    // "Packages/com.xxx.yyy/"
        private string packageName = "";


        private struct PackageInfo
        {
            public string PackageName;              // e.g. "com.foo.bar"
            public List<string> DeclaredDeps;       // e.g. ["com.unity.textmeshpro", ...]
            public string PackageRootUnity;         // e.g. "Packages/com.foo.bar/"
        }

        private enum PackageLoadError
        {
            None,
            InvalidFolderPath,
            MissingPackageJson,
            MissingPackageName,
            UnityDoesNotSeeInstalledPackage,
            Unknown
        }

        private struct PackageLoadResult
        {
            public bool Ok;
            public PackageInfo Info;
            public PackageLoadError Error;
            public string Message;
        }

        private string packageStatus = "No package selected.";

        private readonly HashSet<string> allowedRoots = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> declaredDependencies = new List<string>();

        private readonly List<string> failures = new List<string>();
        private readonly List<string> notes = new List<string>();
        private int scannedAssetCount;
        private int scannedDependencyCount;

        private bool showDeclaredDependencies = true;
        private Vector2 depsScroll;
        private Vector2 scroll;

        private static readonly string[] AssetExtensionsToScan =
        {
            ".prefab", ".unity", ".mat", ".shader", ".shadergraph",
            ".asset", ".controller", ".overrideController",
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff",
            ".fbx", ".wav", ".mp3", ".ogg",
            ".compute", ".cginc", ".hlsl"
        };

        [MenuItem("Tools/Unity Tools/Package Dependency Scanner")]
        public static void Open()
        {
            var w = GetWindow<PackageDependencyChainScannerWindow>("Pkg Dependency Scanner");
            w.minSize = new Vector2(720, 420);
            w.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Package Dependency Scanner", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Step 1: Pick a package folder (must contain package.json)\n" +
                "Step 2: Click Test",
                MessageType.Info);

            EditorGUILayout.Space(8);

            DrawPackagePickerUI();

            EditorGUILayout.Space(10);

            DrawRunUI();

            EditorGUILayout.Space(8);

            DrawDeclaredDependenciesUI();

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(packageStatus, MessageType.None);

            EditorGUILayout.Space(10);
            DrawResultsUI();
        }

        private void DrawPackagePickerUI()
        {
            EditorGUILayout.LabelField("1) Choose package", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                packageRootAbs = EditorGUILayout.TextField("Path", packageRootAbs);
                if (GUILayout.Button("Browse…", GUILayout.Width(90)))
                {
                    var chosen = EditorUtility.OpenFolderPanel(
                        "Select package folder (contains package.json)",
                        string.IsNullOrEmpty(packageRootAbs) ? Application.dataPath : packageRootAbs,// where the select path is initialized
                        "");

                    if (!string.IsNullOrEmpty(chosen))
                    {
                        packageRootAbs = chosen;
                        ReloadSelectedPackage();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload", GUILayout.Width(90)))
                    ReloadSelectedPackage();

                if (GUILayout.Button("Open Folder", GUILayout.Width(100)))
                {
                    if (Directory.Exists(packageRootAbs))
                        EditorUtility.RevealInFinder(packageRootAbs);
                }
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(packageRootUnity)))
            {
                EditorGUILayout.LabelField("Unity Path", packageRootUnity);
                EditorGUILayout.LabelField("Package Name", string.IsNullOrEmpty(packageName) ? "(unknown)" : packageName);
            }
        }

        private void DrawRunUI()
        {
            EditorGUILayout.LabelField("2) Run", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(packageRootUnity)))
            {
                if (GUILayout.Button("Test (Placeholder)", GUILayout.Height(36)))
                {
                    // Placeholder: scanning logic comes next iteration.
                    RunTest();
                }
            }

            if (!string.IsNullOrEmpty(packageStatus))
                EditorGUILayout.HelpBox(packageStatus, MessageType.None);
        }

        private void DrawDeclaredDependenciesUI()
        {
            EditorGUILayout.LabelField("Declared Dependencies (package.json)", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(packageRootUnity)))
            {
                showDeclaredDependencies = EditorGUILayout.Foldout(showDeclaredDependencies,
                    $"Dependencies: {declaredDependencies.Count}", true);

                if (!showDeclaredDependencies) return;

                if (declaredDependencies.Count == 0)
                {
                    EditorGUILayout.HelpBox("(none)", MessageType.None);
                    return;
                }

                // a small scroll area, so long lists won't take over the window
                GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(120));
                depsScroll = GUILayout.BeginScrollView(depsScroll);
                //depsScroll = EditorGUILayout.BeginScrollView(depsScroll, GUILayout.Height(120));

                foreach (var dep in declaredDependencies)
                {
                    EditorGUILayout.LabelField("• " + dep);
                }

                EditorGUILayout.EndScrollView();
                GUILayout.EndVertical();
            }
        }

        private void DrawResultsUI()
        {
            EditorGUILayout.LabelField("Results", EditorStyles.boldLabel);

            if (failures.Count == 0 && scannedAssetCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"PASS ✅  Scanned {scannedAssetCount} assets. Checked {scannedDependencyCount} dependencies.",
                    MessageType.Info);
            }
            else if (failures.Count > 0)
            {
                EditorGUILayout.HelpBox($"FAIL ❌  Found {failures.Count} issue(s).", MessageType.Error);
            }
            else
            {
                EditorGUILayout.HelpBox("No test has been run yet.", MessageType.None);
            }

            if (notes.Count > 0)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Notes:");
                foreach (var n in notes) EditorGUILayout.LabelField("• " + n);
            }

            EditorGUILayout.Space(6);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var f in failures)
            {
                EditorGUILayout.TextArea(f, GUILayout.ExpandHeight(false));
                EditorGUILayout.Space(6);
            }
            EditorGUILayout.EndScrollView();
        }

        private void ReloadSelectedPackage()
        {
            ResetReloadState();

            var result = TryLoadPackageInfo(packageRootAbs);

            if (!result.Ok)
            {
                packageStatus = result.Message;
                // Keep packageRootUnity empty on failure so UI disables relevant sections.
                packageRootUnity = "";
                packageName = "";
                return;
            }
            
            // After making sure the package exists
            // Update window state
            packageName = result.Info.PackageName;
            packageRootUnity = result.Info.PackageRootUnity;

            // Update allowed roots list
            // Add this package itself into allowed roots list
             allowedRoots.Add(packageRootUnity);
            // Add other declared roots
            declaredDependencies.AddRange(result.Info.DeclaredDeps);
            foreach (var dep in declaredDependencies)
                allowedRoots.Add($"Packages/{dep}/");

            packageStatus = "Package loaded.";
            notes.Add($"Declared dependencies: {declaredDependencies.Count}");

        }

        private void ResetReloadState()
        {
            ResetTestData();

            allowedRoots.Clear();
            declaredDependencies.Clear();

            packageRootUnity = "";
            packageName = "";
            packageStatus = "";
        }

        private void ResetTestData()
        {
            failures.Clear();
            notes.Clear();
            scannedAssetCount = 0;
            scannedDependencyCount = 0;
        }

        /// <summary>
        /// Loads package.json from disk and parses name/dependencies (pure IO + string parsing),
        /// then validates Unity sees Packages/{name}/package.json (UnityEditor/AssetDatabase).
        /// </summary>
        private PackageLoadResult TryLoadPackageInfo(string rootAbs)
        {
            try
            {
                // ---- 1) Basic disk checks ----
                if (string.IsNullOrWhiteSpace(rootAbs) || !Directory.Exists(rootAbs))
                {
                    return Fail(PackageLoadError.InvalidFolderPath, "Invalid folder path.");
                }

                var pjAbs = Path.Combine(rootAbs, "package.json");
                if (!File.Exists(pjAbs))
                {
                    return Fail(PackageLoadError.MissingPackageJson, "Selected folder does not contain package.json.");
                }

                // ---- 2) Read once (IO only) ----
                var json = File.ReadAllText(pjAbs);

                // ---- 3) Parse package info (pure string parsing) ----
                if (!TryParsePackageJson(json, out var info, out var parseErrorMsg))
                {
                    return Fail(PackageLoadError.MissingPackageName, parseErrorMsg);
                }

                // ---- 4) Unity validation (AssetDatabase) ----
                // Convert absolute -> Unity Packages path using parsed name
                info.PackageRootUnity = $"Packages/{info.PackageName}/";

                var pjUnity = info.PackageRootUnity + "package.json";
                var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(pjUnity);
                if (textAsset == null)
                {
                    return Fail(
                        PackageLoadError.UnityDoesNotSeeInstalledPackage,
                        "Package name read from package.json, but Unity does not see this package installed.\n" +
                        $"Expected to find: {pjUnity}\n" +
                        "Make sure the project manifest.json references this local package and Unity has imported it."
                    );
                }

                return new PackageLoadResult
                {
                    Ok = true,
                    Info = info,
                    Error = PackageLoadError.None,
                    Message = "OK"
                };
            }
            catch (Exception ex)
            {
                return Fail(PackageLoadError.Unknown, "Failed to load package: " + ex.Message);
            }
        }

        private static PackageLoadResult Fail(PackageLoadError err, string msg)
        {
            return new PackageLoadResult
            {
                Ok = false,
                Info = default,
                Error = err,
                Message = msg ?? "Error"
            };
        }

        private static bool TryParsePackageJson(string json, out PackageInfo info, out string errorMessage)
        {
            info = default;
            errorMessage = "";

            var name = TryReadJsonString(json, "name");
            if (string.IsNullOrEmpty(name))
            {
                errorMessage = "Found package.json but could not read field: name";
                return false;
            }

            var deps = ReadDependenciesWithNewtonsoft(json);

            info.PackageName = name;
            info.DeclaredDeps = deps ?? new List<string>();
            info.PackageRootUnity = ""; // filled by Unity validation step

            return true;
        }

        private static string TryReadJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return "";

            var pattern = $"\"{key}\"";
            var i = json.IndexOf(pattern, StringComparison.Ordinal);
            if (i < 0) return "";

            i = json.IndexOf(':', i);
            if (i < 0) return "";

            i = json.IndexOf('"', i);
            if (i < 0) return "";

            var j = json.IndexOf('"', i + 1);
            if (j < 0) return "";

            return json.Substring(i + 1, j - i - 1);
        }

        private static List<string> TryReadJsonObjectKeys(string json, string key)
        {
            // Best-effort, dependency-free parser:
            // Finds: "<key>": { "a": "...", "b": "..." }
            // Returns ["a","b"].
            var result = new List<string>();
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return result;

            var pattern = $"\"{key}\"";
            var i = json.IndexOf(pattern, StringComparison.Ordinal);
            if (i < 0) return result;

            i = json.IndexOf(':', i);
            if (i < 0) return result;

            // find first '{'
            i = json.IndexOf('{', i);
            if (i < 0) return result;

            // find matching '}'
            int depth = 0;
            int start = -1;
            int end = -1;

            for (int k = i; k < json.Length; k++)
            {
                char c = json[k];
                if (c == '{')
                {
                    if (depth == 0) start = k;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = k;
                        break;
                    }
                }
            }

            if (start < 0 || end < 0 || end <= start) return result;

            var body = json.Substring(start + 1, end - start - 1);

            // parse keys: "some.key": ...
            int p = 0;
            while (p < body.Length)
            {
                // find next quote
                int q1 = body.IndexOf('"', p);
                if (q1 < 0) break;
                int q2 = body.IndexOf('"', q1 + 1);
                if (q2 < 0) break;

                var candidateKey = body.Substring(q1 + 1, q2 - q1 - 1);

                // after q2 should come optional spaces then ':'
                int colon = body.IndexOf(':', q2 + 1);
                if (colon < 0) break;

                // Heuristic: ensure there's no other quote before colon (to reduce false matches)
                // (Simple enough for package.json dependencies)
                result.Add(candidateKey);

                p = colon + 1;
            }

            // de-dup
            return result.Distinct(StringComparer.Ordinal).ToList();
        }
        private static List<string> ReadDependenciesWithNewtonsoft(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<string>();

            var root = JObject.Parse(json);

            var deps = root["dependencies"] as JObject;
            if (deps == null)
                return new List<string>();

            var result = new List<string>();
            foreach (var prop in deps.Properties())
                result.Add(prop.Name); // "com.unity.inputsystem"

            return result;
        }

        private void RunTest()
        {
            ResetTestData();

            if (string.IsNullOrEmpty(packageRootUnity))
            {
                failures.Add("No valid package selected.");
                return;
            }

            if (allowedRoots.Count == 0)
            {
                failures.Add("allowedRoots is empty. Reload the package first (parse package.json).");
                return;
            }

            // 1) Collect scannable assets under the package root
            var guids = AssetDatabase.FindAssets("", new[] { packageRootUnity });
            var assetPaths = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                // I'm not sure if this is good. Whenever the package has an asset, it should be tested.
                .Where(p => p.StartsWith(packageRootUnity, StringComparison.Ordinal))
                .Where(ShouldScanPath)
                .Distinct()
                .ToArray();

            if (assetPaths.Length == 0)
            {
                failures.Add(
                    $"No scannable assets found under: {packageRootUnity}\n" +
                    "Tip: Add assets (prefabs/materials/shaders/textures/etc.) or widen AssetExtensionsToScan.");
                return;
            }

            scannedAssetCount = assetPaths.Length;

            // 2) Scan dependency chain + de-dup issues
            var seenIssues = new HashSet<string>(StringComparer.Ordinal);

            foreach (var owner in assetPaths)
            {
                var deps = AssetDatabase.GetDependencies(owner, recursive: true);
                foreach (var dep in deps)
                {
                    if (dep == owner) continue;
                    scannedDependencyCount++;

                    // Rule: dependency must be under Assets/ or Packages/
                    if (!dep.StartsWith("Assets/", StringComparison.Ordinal) &&
                        !dep.StartsWith("Packages/", StringComparison.Ordinal))
                    {
                        AddIssue(owner, dep,
                            "Dependency path is neither Assets/ nor Packages/ (non-portable reference).",
                            seenIssues);
                        continue;
                    }

                    // Rule: Assets/ forbidden
                    if (dep.StartsWith("Assets/", StringComparison.Ordinal))
                    {
                        AddIssue(owner, dep,
                            "Dependency is under Assets/. Your package would rely on project-local assets.",
                            seenIssues);
                        continue;
                    }

                    // Rule: Packages/ must be allow-listed by package.json
                    if (dep.StartsWith("Packages/", StringComparison.Ordinal))
                    {
                        // For any root in roots, if one satisfies dep.StartsWith, return true
                        bool allowed = allowedRoots.Any(root => dep.StartsWith(root, StringComparison.Ordinal));
                        if (!allowed)
                        {
                            var depRoot = ExtractPackageRoot(dep); // e.g. "Packages/com.unity.textmeshpro/"
                            var hint =
                                string.IsNullOrEmpty(depRoot)
                                    ? "Add this package to package.json dependencies, or remove the reference."
                                    : $"If intentional, add to package.json dependencies: \"{depRoot.Substring("Packages/".Length).TrimEnd('/')}\"";

                            AddIssue(owner, dep,
                                "Dependency is in Packages/ but NOT declared in this package's package.json dependencies.\n" +
                                hint,
                                seenIssues);
                        }
                    }
                }
            }

            notes.Add(failures.Count == 0 ? "Test PASS." : "Test FAIL.");
        }

       private void AddIssue(string owner, string dep, string reason, HashSet<string> seen)
        {
            var key = owner + "\n" + dep + "\n" + reason;
            if (!seen.Add(key)) return;

            failures.Add(
                $"[Dependency Chain Test Failed]\n" +
                $"Owner asset:\n  {owner}\n" +
                $"Bad dependency:\n  {dep}\n" +
                $"Reason:\n  {reason}\n\n" +
                $"Fix:\n" +
                $"  - Move the dependency into the package, OR\n" +
                $"  - Declare the dependency in package.json (recommended), OR\n" +
                $"  - Remove the reference.");
        }

        /// <summary>
        /// Extract "Packages/<package-name>/" from a Unity path like:
        /// "Packages/com.unity.textmeshpro/Scripts/Runtime/TMP_Text.asset"
        /// -> "Packages/com.unity.textmeshpro/"
        /// </summary>
        private static string ExtractPackageRoot(string unityPath)
        {
            if (string.IsNullOrEmpty(unityPath)) return "";
            if (!unityPath.StartsWith("Packages/", StringComparison.Ordinal)) return "";

            var rest = unityPath.Substring("Packages/".Length);
            var slash = rest.IndexOf('/');
            if (slash < 0) return ""; // weird, but safe

            var pkgName = rest.Substring(0, slash);
            return $"Packages/{pkgName}/";
        }
        private static bool ShouldScanPath(string path)
        {
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return false;

            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;

            return AssetExtensionsToScan.Any(x => ext.Equals(x, StringComparison.OrdinalIgnoreCase));
        }
    }
}
#endif