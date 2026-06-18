using System;
using System.IO;
using Autodesk.Navisworks.Api;
using ClashRuleEngine.Models;

namespace ClashRuleEngine.Services
{
    public static class RulePersistenceService
    {
        private const string FileExtension = ".clashre";

        private static string GetConfigFilePath()
        {
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) return null;

            string docPath = doc.FileName;
            if (string.IsNullOrEmpty(docPath))
            {
                string tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClashRuleEngine");
                Directory.CreateDirectory(tempDir);
                return Path.Combine(tempDir, "unsaved_config" + FileExtension);
            }
            return docPath + FileExtension;
        }

        public static void Save(ProjectConfig config)
        {
            try
            {
                string path = GetConfigFilePath();
                if (string.IsNullOrEmpty(path))
                    throw new InvalidOperationException("No active document.");
                File.WriteAllText(path, config.ToXml());
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Failed to save: {ex.Message}",
                    "Clash Rule Engine", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }

        public static ProjectConfig Load()
        {
            try
            {
                string path = GetConfigFilePath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return NewSeeded();
                return ProjectConfig.FromXml(File.ReadAllText(path));   // FromXml seeds
            }
            catch { return NewSeeded(); }
        }

        /// <summary>A fresh, empty project config.</summary>
        public static ProjectConfig NewSeeded()
        {
            return new ProjectConfig();
        }
    }
}
