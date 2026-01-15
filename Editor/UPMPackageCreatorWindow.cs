#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace ShenFengming.UnityTools
{
    public class UPMPackageCreatorWindow : EditorWindow
    {
        private enum PackageType
        {
            Minimal,
            Character
        }

        // --- Minimal settings ---
        private string rootPath;                           // default: current Unity project root
        private string packageName = "com.shen-fengming.newpackage";
        private string displayName = "New Package";
        private string version = "0.1.0";
        private PackageType packageType = PackageType.Minimal;

        // --- Git option ---
        private bool useGit = true;

        [MenuItem("Tools/Unity Tools/UPM Package Creator (Lite)")]
        public static void Open()
        {
            var w = GetWindow<UPMPackageCreatorWindow>("UPM Package Creator (Lite)");
            w.minSize = new Vector2(520, 260);
            w.Show();
        }

        private void OnEnable()
        {
            rootPath = GetProjectRootPath();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("UPM Package Creator (Lite)", EditorStyles.boldLabel);
            EditorGUILayout.Space(6);

            DrawPathSection();
            EditorGUILayout.Space(6);

            DrawPackageSection();
            EditorGUILayout.Space(6);

            useGit = EditorGUILayout.ToggleLeft("Use Git (git init + commit + tag)", useGit);

            EditorGUILayout.Space(10);

            // Disable the create button if the requirements are not satisfied.
            using (new EditorGUI.DisabledScope(!CanCreate()))
            {
                if (GUILayout.Button("Create Package", GUILayout.Height(36)))
                {
                    CreatePackage();
                }
            }
        }

        private void DrawPathSection()
        {
            EditorGUILayout.LabelField("1) Path", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                rootPath = EditorGUILayout.TextField("Root", rootPath);
                if (GUILayout.Button("Browse…", GUILayout.Width(90)))
                {
                    var chosen = EditorUtility.OpenFolderPanel("Select Root Folder", rootPath, "");
                    if (!string.IsNullOrEmpty(chosen))
                        rootPath = chosen;
                }
            }

            if (GUILayout.Button("Use Project Root"))
            {
                rootPath = GetProjectRootPath();
            }
        }

        private void DrawPackageSection()
        {
            EditorGUILayout.LabelField("2) Package", EditorStyles.boldLabel);

            packageName = EditorGUILayout.TextField("Name", packageName);
            displayName = EditorGUILayout.TextField("Display", displayName);
            version = EditorGUILayout.TextField("Version", version);
            packageType = (PackageType)EditorGUILayout.EnumPopup("Type", packageType);
        }

        private bool CanCreate()
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return false;
            if (string.IsNullOrWhiteSpace(packageName) || !packageName.StartsWith("com.")) return false;
            if (string.IsNullOrWhiteSpace(displayName)) return false;
            if (string.IsNullOrWhiteSpace(version)) return false;
            return true;
        }

        private void CreatePackage()
        {
            var packageDir = Path.Combine(rootPath, packageName);

            if (Directory.Exists(packageDir) && Directory.GetFileSystemEntries(packageDir).Length > 0)
            {
                EditorUtility.DisplayDialog(
                    "Folder not empty",
                    $"Target folder already exists and is not empty:\n\n{packageDir}\n\nChoose another name or root.",
                    "OK");
                return;
            }

            try
            {
                Directory.CreateDirectory(packageDir);

                // 5) Create wanted package: always minimal base
                WritePackageJson(packageDir);
                WriteReadme(packageDir);

                CreateBaseFolders(packageDir);
                if (packageType == PackageType.Character)
                    CreateCharacterFolders(packageDir);

                // 3-4) Git support (optional)
                if (useGit)
                {
                    EnsureGitAvailableOrThrow();
                    WriteGitignore(packageDir);
                    RunGit(packageDir, "init");
                    RunGit(packageDir, "add -A");
                    RunGit(packageDir, "commit -m \"Initial package\"");
                    RunGit(packageDir, $"tag v{version}");
                }

                EditorUtility.RevealInFinder(packageDir);

                EditorUtility.DisplayDialog(
                    "Done",
                    $"Created package:\n\n{packageDir}\n\nNext:\n- Install via Package Manager → Add package from disk (package.json)\n- Commit .meta files too (Unity will generate them when imported)",
                    "OK");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                EditorUtility.DisplayDialog("Error", ex.Message, "OK");
            }
        }

        private void CreateBaseFolders(string packageDir)
        {
            Directory.CreateDirectory(Path.Combine(packageDir, "Runtime"));
            Directory.CreateDirectory(Path.Combine(packageDir, "Runtime", "Scripts"));
            Directory.CreateDirectory(Path.Combine(packageDir, "Editor"));
        }

        private void CreateCharacterFolders(string packageDir)
        {
            var art = Path.Combine(packageDir, "Runtime", "Art");
            Directory.CreateDirectory(art);

            Directory.CreateDirectory(Path.Combine(art, "Models"));
            Directory.CreateDirectory(Path.Combine(art, "Materials"));
            Directory.CreateDirectory(Path.Combine(art, "Textures"));
            Directory.CreateDirectory(Path.Combine(art, "Animations"));
            Directory.CreateDirectory(Path.Combine(art, "Prefabs"));
        }

        private void WritePackageJson(string packageDir)
        {
            // Unity baseline: "6000.2" from "6000.2.6f1"
            var unityBaseline = DetectUnityBaseline();

            var json =
$@"{{
  ""name"": ""{EscapeJson(packageName)}"",
  ""displayName"": ""{EscapeJson(displayName)}"",
  ""version"": ""{EscapeJson(version)}"",
  ""unity"": ""{EscapeJson(unityBaseline)}"",
  ""description"": ""{EscapeJson(DescriptionForType())}"",
  ""dependencies"": {{}},
  ""author"": {{
    ""name"": ""Shen-fengming""
  }}
}}";

            File.WriteAllText(Path.Combine(packageDir, "package.json"), json, new UTF8Encoding(false));
        }

        private void WriteReadme(string packageDir)
        {
            var md =
$@"# {displayName}

Package: `{packageName}`
Version: `{version}`

## Install (local)
Unity → Window → Package Manager → + → Add package from disk… → select `package.json`

## Git
Tag: `v{version}`
";
            File.WriteAllText(Path.Combine(packageDir, "README.md"), md, new UTF8Encoding(false));
        }

        private void WriteGitignore(string packageDir)
        {
            var ignore =
@"# OS
.DS_Store

# IDE
.vscode/
.idea/

# Unity generated (if a Unity project is accidentally created here)
[Ll]ibrary/
[Tt]emp/
[Oo]bj/
[Bb]uild/
[Bb]uilds/
[Ll]ogs/
UserSettings/
";
            File.WriteAllText(Path.Combine(packageDir, ".gitignore"), ignore, new UTF8Encoding(false));
        }

        private string DescriptionForType()
        {
            return packageType == PackageType.Character
                ? "Character pack template (Runtime/Art/... + Scripts)."
                : "Minimal Unity package template.";
        }

        private static string DetectUnityBaseline()
        {
            // "6000.2.6f1" -> "6000.2"
            var parts = Application.unityVersion.Split('.');
            if (parts.Length >= 2) return $"{parts[0]}.{parts[1]}";
            // If no version is detected
            return "No version is detected";
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string GetProjectRootPath()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        // --- Git helpers ---

        private static void EnsureGitAvailableOrThrow()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p == null) throw new Exception("Failed to start git. Please install git and ensure it's on PATH.");
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception("git is not available on PATH.");
        }

        private static void RunGit(string workingDir, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workingDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p == null) throw new Exception("Failed to start git process.");

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode != 0)
                throw new Exception($"Git failed: git {args}\n\n{stderr}\n{stdout}");
        }
    }
}
#endif