﻿// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Xml.Linq;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Wox.Infrastructure;
using Wox.Plugin;
using Wox.Plugin.Logger;
using BrowserInfo = Wox.Plugin.Common.DefaultBrowserInfo;

namespace Community.PowerToys.Run.Plugin.Winget
{
    public partial class Main : IPlugin, IPluginI18n, IContextMenu, ISettingProvider, IReloadable, IDisposable
    {
        // Should only be set in Init()
        private Action onPluginError;

        private const string NotGlobalIfUri = nameof(NotGlobalIfUri);

        /// <summary>If true, dont show global result on queries that are URIs</summary>
        private bool _notGlobalIfUri;

        private PluginInitContext _context;

        private string _iconPath;

        private bool _disposed;

        public string Name => Properties.Resources.plugin_name;

        public string Description => Properties.Resources.plugin_description;

        public IEnumerable<PluginAdditionalOption> AdditionalOptions => new List<PluginAdditionalOption>()
        {
            new PluginAdditionalOption()
            {
                Key = NotGlobalIfUri,
                DisplayLabel = Properties.Resources.plugin_global_if_uri,
                Value = false,
            },
        };

        public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
        {
            return new List<ContextMenuResult>(0);
        }

        public List<Result> Query(Query query)
        {
            if (query is null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            var results = new List<Result>();

            // empty query
            if (string.IsNullOrEmpty(query.Search))
            {
                string arguments = "winget ";
                results.Add(new Result
                {
                    Title = Properties.Resources.plugin_description.Remove(Description.Length - 1, 1),
                    SubTitle = string.Format(CultureInfo.CurrentCulture, Properties.Resources.plugin_in_browser_name, BrowserInfo.Name ?? BrowserInfo.MSEdgeName),
                    QueryTextDisplay = string.Empty,
                    IcoPath = _iconPath,
                    ProgramArguments = arguments,
                    Action = action =>
                    {
                        if (!Helper.OpenCommandInShell(BrowserInfo.Path, BrowserInfo.ArgumentsPattern, arguments))
                        {
                            onPluginError();
                            return false;
                        }

                        return true;
                    },
                });
                return results;
            }
            else
            {
                string searchTerm = query.Search;

                Process process = new Process();

                process.StartInfo.FileName = "winget";
                process.StartInfo.Arguments = $"search \"{searchTerm}\"";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.Start();

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                var bytes = System.Text.Encoding.Default.GetBytes(output);

                // UTF16 to UTF8
                output = System.Text.Encoding.UTF8.GetString(
                    System.Text.Encoding.Convert(
                        System.Text.Encoding.Unicode,
                        System.Text.Encoding.UTF8,
                        System.Text.Encoding.Unicode.GetBytes(output)));

                // If there is no error, iterate through the output and add each line as a result
                string[] lines = output.Split(new[] { Environment.NewLine }, StringSplitOptions.None);

                var id = 0;

                int nameChars = 0;
                int idChars = 0;
                int versionChars = 0;
                int matchChars = 0;

                // Regex for words in header
                string v = lines[0];
                var matches = Regex.Matches(v, @"\S+");

                if (matches != null)
                {
                    // Get chars between Name, ID, Version, Matches, Source length including spaces
                    if (matches.Count == 5)
                    {
                        nameChars = matches[2].Index - matches[1].Index;
                        idChars = matches[3].Index - matches[2].Index;
                        versionChars = matches[4].Index - matches[3].Index;
                    }
                    else if (matches.Count == 6)
                    {
                        nameChars = matches[2].Index - matches[1].Index;
                        idChars = matches[3].Index - matches[2].Index;
                        versionChars = matches[4].Index - matches[3].Index;
                        matchChars = matches[5].Index - matches[4].Index;
                    }
                }

                foreach (string line0 in lines)
                {
                    // Skip header
                    if (id < 2)
                    {
                        id++;
                        continue;
                    }

                    // Filter non-text, non-number, non-space and non (-_.,) characters
                    var line = AllowedCharacters().Replace(line0, string.Empty);

                    if (line != string.Empty)
                    {
                        string name = "_";
                        string idStr = "_";
                        string version = "_";
                        string match = "_";
                        string source = "_";
                        try
                        {
                            // Header: Name                         ID                            Version Übereinstimmung   Quelle
                            // Divide line into 5 parts by split
                            name = line.Substring(0, nameChars).Trim();
                            idStr = line.Substring(nameChars, idChars).Trim();
                            version = line.Substring(idChars, versionChars).Trim();
                            if (matches.Count == 6)
                            {
                                match = line.Substring(versionChars, matchChars).Trim();
                                source = line.Substring(matchChars).Trim();
                            }
                            else
                            {
                                match = string.Empty;
                            }

                            // name = ";" + lines[0].Split("ID")[0].Length.ToString() + ";"; // matches[1].Index;;
                            // idStr = idChars.ToString();
                            // version = versionChars.ToString();
                            // match = matchChars.ToString();
                            // source = sourceChars.ToString();
                        }
                        catch (Exception e)
                        {
                            name = e.ToString();
                        }

                        string title = $"{name} ({idStr})";
                        // string subTitle = $"{Properties.Resources.plugin_result_name} {version} ({source}) {match}";
                        string subTitle = $"";
                        results.Add(new Result
                        {
                            Title = title,
                            SubTitle = subTitle,
                            QueryTextDisplay = string.Empty,
                            IcoPath = _iconPath,
                            ProgramArguments = "winget " + id,
                            Action = action =>
                            {
                                Helper.OpenInShell("winget", "install " + idStr, "/");

                                return true;
                            },
                        });
                        id++;
                    }
                }
            }

            return results;
        }

        public void Init(PluginInitContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _context.API.ThemeChanged += OnThemeChanged;
            UpdateIconPath(_context.API.GetCurrentTheme());
            BrowserInfo.UpdateIfTimePassed();

            onPluginError = () =>
            {
                string errorMsgString = string.Format(CultureInfo.CurrentCulture, Properties.Resources.plugin_search_failed, BrowserInfo.Name ?? BrowserInfo.MSEdgeName);

                Log.Error(errorMsgString, GetType());
                _context.API.ShowMsg(
                    $"Plugin: {Properties.Resources.plugin_name}",
                    errorMsgString);
            };
        }

        public string GetTranslatedPluginTitle()
        {
            return Properties.Resources.plugin_name;
        }

        public string GetTranslatedPluginDescription()
        {
            return Properties.Resources.plugin_description;
        }

        private void OnThemeChanged(Theme oldtheme, Theme newTheme)
        {
            UpdateIconPath(newTheme);
        }

        private void UpdateIconPath(Theme theme)
        {
            if (theme == Theme.Light || theme == Theme.HighContrastWhite)
            {
                _iconPath = "Images/WebSearch.light.png";
            }
            else
            {
                _iconPath = "Images/WebSearch.dark.png";
            }
        }

        public Control CreateSettingPanel()
        {
            throw new NotImplementedException();
        }

        public void UpdateSettings(PowerLauncherPluginSettings settings)
        {
            _notGlobalIfUri = settings?.AdditionalOptions?.FirstOrDefault(x => x.Key == NotGlobalIfUri)?.Value ?? false;
        }

        public void ReloadData()
        {
            if (_context is null)
            {
                return;
            }

            UpdateIconPath(_context.API.GetCurrentTheme());
            BrowserInfo.UpdateIfTimePassed();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                if (_context != null && _context.API != null)
                {
                    _context.API.ThemeChanged -= OnThemeChanged;
                }

                _disposed = true;
            }
        }

        [GeneratedRegex("[^\\u0020-\\u007E]")]
        private static partial Regex AllowedCharacters();
    }
}