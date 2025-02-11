/*
 *
 * (c) Copyright Ascensio System Limited 2010-2021
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * http://www.apache.org/licenses/LICENSE-2.0
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/


using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using ASC.Common.Data;
using ASC.Common.Logging;
using ASC.Data.Backup.Tasks.Modules;
using ASC.Data.Storage;

namespace ASC.Data.Backup.Tasks
{
    public class ProgressChangedEventArgs : EventArgs
    {
        public int Progress { get; private set; }

        public ProgressChangedEventArgs(int progress)
        {
            Progress = progress;
        }
    }

    public abstract class PortalTaskBase
    {
        protected const int TasksLimit = 10;
        protected readonly List<ModuleName> IgnoredModules = new List<ModuleName>();
        protected readonly List<string> IgnoredTables = new List<string>(); //todo: add using to backup and transfer tasks

        protected ILog Logger;

        public int Progress { get; private set; }

        public int TenantId { get; private set; }
        public string ConfigPath { get; private set; }

        public bool ProcessStorage { get; set; }

        protected PortalTaskBase(ILog logger, int tenantId, string configPath)
        {
            Logger = logger ?? LogManager.GetLogger("ASC");
            TenantId = tenantId;
            ConfigPath = configPath;
            ProcessStorage = true;
        }

        protected string RegisterDatabase(int id, string connectionString)
        {
            connectionString = connectionString + ";convert zero datetime=True";
            var connectionSettings = new ConnectionStringSettings("mailservice-" + id, connectionString, "MySql.Data.MySqlClient");

            if (DbRegistry.IsDatabaseRegistered(connectionSettings.Name))
            {
                DbRegistry.UnRegisterDatabase(connectionSettings.Name);
            }

            DbRegistry.RegisterDatabase(connectionSettings.Name, connectionSettings);
            return connectionSettings.Name;
        }

        public void IgnoreModule(ModuleName moduleName)
        {
            if (!IgnoredModules.Contains(moduleName))
                IgnoredModules.Add(moduleName);
        }

        public void IgnoreTable(string tableName)
        {
            if (!IgnoredTables.Contains(tableName))
                IgnoredTables.Add(tableName);
        }

        public abstract void RunJob();

        internal virtual IEnumerable<IModuleSpecifics> GetModulesToProcess()
        {
            return ModuleProvider.AllModules.Where(module => !IgnoredModules.Contains(module.ModuleName));
        }

        protected IEnumerable<BackupFileInfo> GetFilesToProcess(int tenantId)
        {
            var files = new List<BackupFileInfo>();
            foreach (var module in StorageFactory.GetModuleList(ConfigPath).Where(IsStorageModuleAllowed))
            {
                var store = StorageFactory.GetStorage(ConfigPath, tenantId.ToString(), module);
                var domains = StorageFactory.GetDomainList(ConfigPath, module).ToArray();

                foreach (var domain in domains)
                {
                    files.AddRange(
                        store.ListFilesRelative(domain, "\\", "*.*", true)
                        .Select(path => new BackupFileInfo(domain, module, path, tenantId)));
                }

                files.AddRange(
                    store.ListFilesRelative(string.Empty, "\\", "*.*", true)
                         .Where(path => domains.All(domain => !path.Contains(domain + "/")))
                         .Select(path => new BackupFileInfo(string.Empty, module, path, tenantId)));
            }

            return files.Distinct();
        }

        protected bool IsStorageModuleAllowed(string storageModuleName)
        {
            var allowedStorageModules = new List<string>
                {
                    "forum",
                    "photo",
                    "bookmarking",
                    "wiki",
                    "files",
                    "crm",
                    "projects",
                    "logo",
                    "fckuploaders",
                    "talk",
                    "mailaggregator",
                    "whitelabel",
                    "customnavigation",
                    "userPhotos"
                };

            if (!allowedStorageModules.Contains(storageModuleName))
                return false;

            IModuleSpecifics moduleSpecifics = ModuleProvider.GetByStorageModule(storageModuleName);
            return moduleSpecifics == null || !IgnoredModules.Contains(moduleSpecifics.ModuleName);
        }

        #region Progress

        public event EventHandler<ProgressChangedEventArgs> ProgressChanged;

        private int stepsCount = 1;
        private volatile int stepsCompleted;

        protected void SetStepsCount(int value)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException("value");
            }
            stepsCount = value;
            Logger.Debug("Steps: " + stepsCount);
        }

        protected void SetStepCompleted(int increment = 1)
        {
            if (stepsCount == 1)
            {
                return;
            }
            if (stepsCompleted == stepsCount)
            {
                throw new InvalidOperationException("All steps completed.");
            }
            stepsCompleted += increment;
            SetProgress(100 * stepsCompleted / stepsCount);
        }

        protected void SetCurrentStepProgress(int value)
        {
            if (value < 0 || value > 100)
            {
                throw new ArgumentOutOfRangeException("value");
            }
            if (value == 100)
            {
                SetStepCompleted();
            }
            else
            {
                SetProgress((100 * stepsCompleted + value) / stepsCount);
            }
        }

        protected void SetProgress(int value)
        {
            if (value < 0 || value > 100)
            {
                throw new ArgumentOutOfRangeException("value");
            }
            if (Progress != value)
            {
                Progress = value;
                OnProgressChanged(new ProgressChangedEventArgs(value));
            }
        }

        protected virtual void OnProgressChanged(ProgressChangedEventArgs eventArgs)
        {
            var handler = ProgressChanged;
            if (handler != null)
            {
                handler(this, eventArgs);
            }
        }

        #endregion

        protected Dictionary<string, string> ParseConnectionString(string connectionString)
        {
            var result = new Dictionary<string, string>();

            var parsed = connectionString.Split(';');

            foreach (var p in parsed)
            {
                if (string.IsNullOrEmpty(p.Trim())) continue;
                var keyValue = p.Split('=');
                result.Add(keyValue[0].ToLowerInvariant(), keyValue[1]);
            }

            return result;
        }

        protected async Task RunMysqlFile(Stream stream, string db, string delimiter = ";")
        {
            using (var dbManager = DbManager.FromHttpContext(db, 100000))
            using (var tr = dbManager.BeginTransaction())
            {
                await dbManager.ExecuteNonQueryAsync("SET FOREIGN_KEY_CHECKS=0;");
                if (stream == null) return;

                using (var reader = new StreamReader(stream))
                {
                    string commandText;

                    if (delimiter != null)
                    {
                        while ((commandText = await reader.ReadLineAsync()) != null)
                        {
                            var firstText = commandText;
                            while (!commandText.EndsWith(delimiter))
                            {
                                var newline = await reader.ReadLineAsync();
                                if (newline == null)
                                {
                                    break;
                                }
                                commandText += newline;
                            }

                            try
                            {
                                commandText = commandText.Replace("\\r", "\r").Replace("\\n", "\n");
                                await dbManager.ExecuteNonQueryAsync(commandText, null);
                            }
                            catch (Exception)
                            {
                                try
                                {
                                    if (commandText.StartsWith("REPLACE INTO"))
                                    {
                                        var innerValues = commandText.Split(',').ToList();
                                        for (int i = 0; i < innerValues.Count(); i++)
                                        {
                                            var flag1 = false;
                                            var flag2 = false;
                                            if (innerValues[i].StartsWith("("))
                                            {
                                                flag1 = true;
                                                innerValues[i] = innerValues[i].TrimStart('(');
                                            }
                                            else if (innerValues[i].EndsWith(")") && !innerValues[i].StartsWith("'")
                                                || innerValues[i].EndsWith("')") && innerValues[i] != "')")
                                            {
                                                flag2 = true;
                                                innerValues[i] = innerValues[i].TrimEnd(')');
                                            }
                                            if (i == innerValues.Count() - 1)
                                            {
                                                innerValues[i] = innerValues[i].Remove(innerValues[i].Length - 2, 2);
                                            }
                                            if (innerValues[i].StartsWith("\'") && ((!innerValues[i].EndsWith("\'") || innerValues[i] == "'")
                                                || i != innerValues.Count() - 1 && (!innerValues[i + 1].StartsWith("\'") && innerValues[i + 1].EndsWith("\'") && !innerValues[i + 1].StartsWith("(\'") || innerValues[i + 1] == "'")))
                                            {
                                                innerValues[i] += "," + innerValues[i + 1];
                                                innerValues.RemoveAt(i + 1);
                                            }
                                            if (innerValues[i].StartsWith("\'") && innerValues[i].EndsWith("\'"))
                                            {
                                                if (innerValues[i] != "''")
                                                {
                                                    var sw = new StringWriter();
                                                    sw.Write("0x");
                                                    foreach (var b in Encoding.UTF8.GetBytes(innerValues[i].Trim('\'')))
                                                        sw.Write("{0:x2}", b);
                                                    innerValues[i] = string.Format("CONVERT({0} USING utf8)", sw.ToString());
                                                }
                                            }
                                            if (flag1)
                                            {
                                                innerValues[i] = "(" + innerValues[i];
                                            }
                                            else if (flag2)
                                            {
                                                innerValues[i] = innerValues[i] + ")";
                                            }
                                            if (i == innerValues.Count() - 1)
                                            {
                                                innerValues[i] = innerValues[i] + ");";
                                            }
                                        }
                                        commandText = string.Join(",", innerValues).ToString();
                                        await dbManager.ExecuteNonQueryAsync(commandText, null);
                                    }
                                    else
                                    {
                                        Thread.Sleep(1000);//avoiding deadlock
                                        await dbManager.ExecuteNonQueryAsync(commandText, null);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error("Restore", ex);
                                }
                            }
                        }
                    }
                    else
                    {
                        commandText = await reader.ReadToEndAsync();

                        try
                        {
                            await dbManager.ExecuteNonQueryAsync(commandText, null);
                        }
                        catch (Exception e)
                        {
                            Logger.Error("Restore", e);
                        }
                    }
                }

                tr.Commit();
            }
        }
        protected async Task RunMysqlProcedure(Stream stream)
        {
            using (var dbManager = DbManager.FromHttpContext("default", 100000))
            {
                if (stream == null) return;

                string commandText;
                string delimiter;

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var data = await reader.ReadLineAsync();
                    var re = new Regex(@"DELIMITER (\S+)");
                    var match = re.Match(data);

                    if (match.Success)
                    {
                        delimiter = match.Groups[1].Value;
                    }
                    else
                    {
                        return;
                    }

                    while ((commandText = await reader.ReadLineAsync()) != null)
                    {
                        while (!commandText.EndsWith(delimiter))
                        {
                            var newline = await reader.ReadLineAsync();
                            if (newline == null)
                            {
                                break;
                            }
                            commandText += newline;
                        }

                        try
                        {
                            commandText = commandText.Replace(delimiter, "").Trim();
                            if (!commandText.StartsWith("DELIMITER"))
                            {
                                await dbManager.ExecuteNonQueryAsync(commandText, null);
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error("Restore", e);
                        }
                    }
                }
            }
        }
    }
}
