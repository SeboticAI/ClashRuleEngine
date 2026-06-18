using System;
using System.IO;
using ClashRuleEngine.Models;

namespace ClashRuleEngine.Services
{
    public static class RulePersistenceService
    {
        private const string FileExtension = ".clashre";

        /// <summary>The GLOBAL config — one per user, persists across files AND Navisworks
        /// instances. This is the single source of truth, so an imported rule set survives
        /// until a new import (or edit) overwrites it, regardless of which model is open.</summary>
        private static string GlobalConfigPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClashRuleEngine");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config" + FileExtension);
        }

        public static void Save(ProjectConfig config)
        {
            try
            {
                File.WriteAllText(GlobalConfigPath(), config.ToXml());   // survives files + instances
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
                string global = GlobalConfigPath();
                if (!File.Exists(global)) return NewSeeded();
                return ProjectConfig.FromXml(File.ReadAllText(global));   // FromXml seeds
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
