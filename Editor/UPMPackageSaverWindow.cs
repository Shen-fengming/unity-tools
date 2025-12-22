#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace ShenFengming.UnityTools
{
    public class UPMPackageSaverWindow : EditorWindow
    {
        private string packageRoot = "";
        private string packageName = "";
        private string currentVersion = "";
        private string nextVersion = "";
        private bool hasGit = false;
        private string status = "Pick a package folder (must contain package.json).";
        private string commitMessage = "";

        [MenuItem("Tools/Unity Tools/UPM Package Saver (Lite)")]
        public static void Open()
        {
            var w = GetWindow<UPMPackageSaverWindow>("UPM Package Saver (Lite)");
            w.minSize = new Vector2(560, 240);
            w.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("UPM Package Saver (Lite)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Auto bump PATCH version → update package.json → git add/commit/tag", EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(10);
            DrawPickSection();

            EditorGUILayout.Space(10);
            DrawInfoSection();

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Commit Message", EditorStyles.boldLabel);
            commitMessage = EditorGUILayout.TextField(commitMessage);

            EditorGUILayout.Space(12);
            using (new EditorGUI.DisabledScope(!CanSave()))
            {
                if (GUILayout.Button("Save (git add/commit/tag + bump patch)", GUILayout.Height(40)))
                {
                    Save();
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(status, MessageType.Info);
        }

        private void DrawPickSection()
        {
            EditorGUILayout.LabelField("1) Choose package", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                packageRoot = EditorGUILayout.TextField("Path", packageRoot);
                if (GUILayout.Button("Browse…", GUILayout.Width(90)))
                {
                    var chosen = EditorUtility.OpenFolderPanel("Select package folder (contains package.json)", packageRoot, "");
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        packageRoot = chosen;
                        Reload();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload"))
                    Reload();

                if (GUILayout.Button("Open Folder"))
                {
                    if (Directory.Exists(packageRoot))
                        EditorUtility.RevealInFinder(packageRoot);
                }
            }
        }

        private void DrawInfoSection()
        {
            EditorGUILayout.LabelField("2) Detected info", EditorStyles.boldLabel);

            EditorGUILayout.LabelField($"Package: {(string.IsNullOrEmpty(packageName) ? "-" : packageName)}");
            EditorGUILayout.LabelField($"Current version: {(string.IsNullOrEmpty(currentVersion) ? "-" : currentVersion)}");
            EditorGUILayout.LabelField($"Next version (patch): {(string.IsNullOrEmpty(nextVersion) ? "-" : nextVersion)}");
            EditorGUILayout.LabelField($"Git repo: {(hasGit ? "YES" : "NO")}");

            if (!hasGit && !string.IsNullOrEmpty(packageRoot))
            {
                EditorGUILayout.LabelField("Save is disabled because this folder is not a git repo (.git missing).", EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void Reload()
        {
            try
            {
                packageName = "";
                currentVersion = "";
                nextVersion = "";
                hasGit = false;

                if (string.IsNullOrWhiteSpace(packageRoot) || !Directory.Exists(packageRoot))
                {
                    status = "Invalid folder path.";
                    return;
                }

                var pj = Path.Combine(packageRoot, "package.json");
                if (!File.Exists(pj))
                {
                    status = "No package.json found in this folder.";
                    return;
                }

                var json = File.ReadAllText(pj);
                packageName = ExtractJsonString(json, "name");
                currentVersion = ExtractJsonString(json, "version");

                if (!IsSemVer(currentVersion))
                {
                    status = "Version in package.json is not SemVer (expected X.Y.Z).";
                    return;
                }

                nextVersion = BumpPatch(currentVersion);
                commitMessage = $"Bump version to v{nextVersion}";

                hasGit = Directory.Exists(Path.Combine(packageRoot, ".git"));
                status = hasGit
                    ? "Ready: click Save to bump version + git commit + tag."
                    : "This package has no .git folder. Initialize git first to enable Save.";
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                status = "Error while reading package info: " + ex.Message;
            }
        }

        private bool CanSave()
        {
            if (string.IsNullOrWhiteSpace(packageRoot)) return false;
            if (!Directory.Exists(packageRoot)) return false;
            if (!File.Exists(Path.Combine(packageRoot, "package.json"))) return false;
            if (!hasGit) return false;
            if (!IsSemVer(currentVersion)) return false;
            if (!IsGitAvailable()) return false;
            return true;
        }

        private void Save()
        {
            try
            {
                // Re-read to avoid saving stale info
                Reload();
                if (!CanSave())
                {
                    EditorUtility.DisplayDialog("Cannot Save", status, "OK");
                    return;
                }

                // 1) Update package.json version
                UpdatePackageJsonVersion(packageRoot, nextVersion);

                // 2) Git add/commit/tag
                RunGit(packageRoot, "add -A");

                if (string.IsNullOrWhiteSpace(commitMessage))
                    throw new Exception("Commit message cannot be empty.");

                RunGit(packageRoot, $"commit -m {Quote(commitMessage)}");

                RunGit(packageRoot, $"tag v{nextVersion}");

                // Refresh UI
                Reload();

                EditorUtility.DisplayDialog("Done", $"Saved {packageName} → v{nextVersion}", "OK");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                EditorUtility.DisplayDialog("Error", ex.Message, "OK");
            }
        }

        // ---------- Minimal helpers ----------

        private static string ExtractJsonString(string json, string key)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static bool IsSemVer(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            return Regex.IsMatch(v, @"^\d+\.\d+\.\d+$");
        }

        private static string BumpPatch(string v)
        {
            var parts = v.Split('.');
            int major = int.Parse(parts[0]);
            int minor = int.Parse(parts[1]);
            int patch = int.Parse(parts[2]) + 1;
            return $"{major}.{minor}.{patch}";
        }

        private static void UpdatePackageJsonVersion(string packageRoot, string newVersion)
        {
            var pj = Path.Combine(packageRoot, "package.json");
            var json = File.ReadAllText(pj);

            var regex = new Regex("\"version\"\\s*:\\s*\"([^\"]*)\"");
            var match = regex.Match(json);

            if (!match.Success)
                throw new Exception("package.json does not contain a version field.");

            var updated =
                json.Substring(0, match.Index)
                + $"\"version\": \"{newVersion}\""
                + json.Substring(match.Index + match.Length);

            File.WriteAllText(pj, updated, new UTF8Encoding(false));
        }

        private static bool IsGitAvailable()
        {
            try
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
                if (p == null) return false;
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch { return false; }
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

        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            // Safe enough for commit messages
            return $"\"{s.Replace("\"", "\\\"")}\"";
        }
    }
}
#endif