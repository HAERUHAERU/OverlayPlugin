﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using SharpCompress.Archives.SevenZip;

namespace RainbowMage.OverlayPlugin.Updater
{
    public class Installer
    {
        ProgressDisplay _display;
        string _tempDir = null;
        string _destDir = null;

        public ProgressDisplay Display => _display;

        public Installer(string dest)
        {
            _display = new ProgressDisplay();
            _display.Show();

            _destDir = dest;
            // Make sure our temporary directory is on the same drive as the destination.
            _tempDir = Path.Combine(Path.GetDirectoryName(dest), "OverlayPlugin.tmp");
        }

        public static async Task<bool> Run(string url, string _destDir, bool overwrite = false)
        {
            var inst = new Installer(_destDir);

            // We need to use a Task here since parts of Download() and the other methods are blocking.
            return await Task.Run(async () =>
            {
                var result = false;
                var archivePath = Path.Combine(inst._tempDir, "update.7z");

                if (await inst.Download(url, archivePath) && inst.Extract(archivePath))
                {
                    result = overwrite ? inst.InstallOverwrite() : inst.InstallReplace();
                    inst.Cleanup();

                    if (result)
                    {
                        inst.Display.Close();
                    }
                }

                return result;
            });
        }

        public static async Task<bool> InstallMsvcrt()
        {
            var inst = new Installer(Path.Combine(Path.GetTempPath(), "OverlayPlugin.tmp"));
            var exePath = Path.Combine(inst._tempDir, "vc_redist.x64.exe");

            return await Task.Run(async () =>
            {
                if (await inst.Download("https://aka.ms/vs/16/release/VC_redist.x64.exe", exePath))
                {
                    inst.Display.UpdateStatus(0, string.Format(Resources.StatusLaunchingInstaller, 2, 2));
                    inst.Display.Log(Resources.LogLaunchingInstaller);

                    try
                    {
                        var proc = Process.Start(exePath);
                        proc.WaitForExit();
                        proc.Close();
                    } catch(System.ComponentModel.Win32Exception ex)
                    {
                        inst.Display.Log(string.Format(Resources.LaunchingInstallerFailed, ex.Message));
                        inst.Display.Log(Resources.LogRetry);

                        using (var proc = new Process())
                        {
                            proc.StartInfo.FileName = exePath;
                            proc.StartInfo.UseShellExecute = true;
                            proc.Start();
                        }

                        var cancel = inst.Display.GetCancelToken();

                        inst.Display.Log(Resources.LogInstallerWaiting);
                        while (!File.Exists("C:\\Windows\\system32\\msvcp140.dll") && !cancel.IsCancellationRequested)
                        {
                            Thread.Sleep(500);
                        }

                        // Wait some more just to be sure that the installer is done.
                        Thread.Sleep(1000);
                    }

                    inst.Cleanup();
                    if (File.Exists("C:\\Windows\\system32\\msvcp140.dll"))
                    {
                        inst.Display.Close();
                        return true;
                    } else
                    {
                        inst.Display.UpdateStatus(1, Resources.StatusError);
                        inst.Display.Log(Resources.LogInstallerFailed);
                        return false;
                    }
                }

                return false;
            });
        }

        public async Task<bool> Download(string url, string dest)
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }

                Directory.CreateDirectory(_tempDir);
            } catch (Exception ex)
            {
                _display.Log(string.Format(Resources.CreatingTempDirFailed, _tempDir, ex));
                return false;
            }

            _display.UpdateStatus(0, string.Format(Resources.StatusDownloadStarted, 1, 2));
            _display.Log(string.Format(Resources.LogDownloading, url, dest));

            var success = false;
            var client = new HttpClient();
            var cancel = _display.GetCancelToken();
            HttpResponseMessage response;

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            client.DefaultRequestHeaders.Add("User-Agent", "ngld/OverlayPlugin v" + currentVersion.ToString());

            try
            {
                Stream stream = null;
                var buffer = new byte[81290];
                var read = 0;
                var failed = true;
                var retries = 10;

                using (var file = File.OpenWrite(dest))
                {
                    while (retries > 0 && failed)
                    {
                        failed = false;
                        retries--;

                        try
                        {
                            response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
                        }
                        catch (HttpRequestException ex)
                        {
                            _display.Log(string.Format(Resources.LogDownloadFailed, ex));

                            if (retries > 0)
                            {
                                _display.Log(Resources.LogRetryAfter1s);
                                Thread.Sleep(1000);
                            }
                            failed = true;
                            continue;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            _display.Log(string.Format(Resources.LogDownloadFailed, response.ReasonPhrase));

                            if (retries > 0)
                            {
                                _display.Log(Resources.LogRetryAfter1s);
                                Thread.Sleep(1000);
                            }
                            failed = true;
                            continue;
                        }

                        var length = response.Content.Headers.ContentLength ?? -1;
                        if (length == -1)
                        {
                            // Retrying wouldn't help here.
                            _display.Log(Resources.DownloadFailedContentLengthMissing);
                            return false;
                        }

                        if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            // This is a resumed download.
                            length += file.Position;
                        }
                        else
                        {
                            // Make sure we don't append stuff if the resumption failed and we're receiving the whole file again.
                            file.Seek(0, SeekOrigin.Begin);
                        }

                        stream = await response.Content.ReadAsStreamAsync();

                        var status = string.Format(Resources.StatusDownloadStarted, 1, 2);
                        try
                        {
                            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                file.Write(buffer, 0, read);
                                _display.UpdateStatus((float)file.Position / length, status);

                                if (cancel.IsCancellationRequested)
                                {
                                    retries = 0;
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _display.Log(string.Format(Resources.LogDownloadInterrupted, ex));

                            if (file.Position > 0 && retries > 0)
                            {
                                _display.Log(Resources.LogResumingDownload);
                                failed = true;

                                client.DefaultRequestHeaders.Remove("Range");
                                client.DefaultRequestHeaders.Add("Range", $"bytes={file.Position}-");
                                continue;
                            }
                        }

                        success = true;
                        break;
                    }
                }

                if (cancel.IsCancellationRequested || failed)
                {
                    _display.UpdateStatus(0, Resources.StatusCancelling);
                    File.Delete(dest);

                    if (failed)
                    {
                        _display.UpdateStatus(0, Resources.OutOfRetries);
                        _display.Log(Resources.OutOfRetries);
                    }
                    else
                    {
                        _display.UpdateStatus(0, Resources.StatusCancelled);
                        _display.Log(Resources.LogAbortedByUser);
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                if (cancel.IsCancellationRequested)
                {
                    _display.UpdateStatus(1, Resources.StatusCancelled);
                    _display.Log(Resources.LogAbortedByUser);
                    return false;
                }

                _display.Log(string.Format(Resources.Exception, ex));
                return false;
            }
            finally
            {
                _display.DisposeCancelSource();
                client.Dispose();

                if (!success) Cleanup();
            }

            return true;
        }

        public bool Extract(string archivePath)
        {
            var success = false;
            var cancel = _display.GetCancelToken();

            try
            {
                _display.UpdateStatus(0, string.Format(Resources.StatusPreparingExtraction, 2, 2));
                _display.Log(Resources.LogOpeningArchive);

                var contentsPath = Path.Combine(_tempDir, "contents");
                Directory.CreateDirectory(contentsPath);

                using (var archive = SevenZipArchive.Open(archivePath))
                {
                    // Make sure we never divide by zero.
                    var total = 1d;
                    var done = 0d;

                    foreach (var entry in archive.Entries)
                    {
                        total += entry.Size;
                    }

                    using (var reader = archive.ExtractAllEntries())
                    {
                        reader.EntryExtractionProgress += (sender, e) =>
                        {
                            var percent = (float)(done + e.ReaderProgress.BytesTransferred) / total;

                            _display.UpdateStatus(Math.Min(percent, 1), $"[2/2]: {reader.Entry.Key}");
                        };

                        cancel = _display.GetCancelToken();
                        _display.Log(Resources.LogExtractingFiles);

                        while (reader.MoveToNextEntry())
                        {
                            if (cancel.IsCancellationRequested)
                            {
                                break;
                            }

                            var outPath = Path.Combine(contentsPath, reader.Entry.Key);

                            if (reader.Entry.IsDirectory)
                            {
                                if (!Directory.Exists(outPath))
                                {
                                    Directory.CreateDirectory(outPath);
                                }
                            }
                            else
                            {
                                using (var writer = File.OpenWrite(outPath))
                                {
                                    reader.WriteEntryTo(writer);
                                }
                            }

                            done += reader.Entry.Size;
                        }
                    }
                }

                if (cancel.IsCancellationRequested)
                {
                    _display.UpdateStatus(1, Resources.StatusCancelled);
                    _display.Log(Resources.LogExtractionAbortedByUser);
                    return false;
                }

                success = true;
            } catch(Exception ex)
            {
                if (cancel.IsCancellationRequested)
                {
                    _display.UpdateStatus(1, Resources.StatusCancelled);
                    _display.Log(Resources.LogAbortedByUser);
                    return false;
                }

                _display.Log(string.Format(Resources.Exception, ex));
                return false;
            }
            finally
            {
                _display.DisposeCancelSource();

                if (!success) Cleanup();
            }

            _display.UpdateStatus(1, string.Format(Resources.StatusDone, 2, 2));
            _display.Log(Resources.LogDone);
            return success;
        }

        public bool InstallReplace()
        {
            try
            {
                string backup = null;
                var parent = Path.GetDirectoryName(_destDir);
                if (!Directory.Exists(parent))
                    Directory.CreateDirectory(parent);

                if (Directory.Exists(_destDir))
                {
                    _display.Log(Resources.LogBackingUpOldFiles);

                    backup = _destDir + ".bak";
                    if (Directory.Exists(backup))
                        Directory.Delete(backup, true);

                    Directory.Move(_destDir, backup);
                }

                try
                {
                    _display.Log(Resources.LogMovingDirectory);
                    Directory.Move(Path.Combine(_tempDir, "contents"), _destDir);
                }
                catch (Exception e)
                {
                    _display.Log(string.Format(Resources.LogFailedReplaceDirectory, e));
                    _display.Log(Resources.LogCleaningUp);

                    if (Directory.Exists(_destDir))
                    {
                        Directory.Delete(_destDir, true);
                    }

                    if (backup != null)
                    {
                        _display.Log(Resources.LogRestoringBackup);

                        Directory.Move(backup, _destDir);
                    }

                    _display.Log(Resources.LogDone);
                    return false;
                }

                if (backup != null)
                {
                    _display.Log(Resources.LogRemovingOldBackup);
                    Directory.Delete(backup, true);
                }

                return true;
            }
            catch (Exception e)
            {
                _display.Log(string.Format(Resources.Exception, e));
                return false;
            }
        }

        public bool InstallOverwrite()
        {
            try
            {
                try
                {
                    _display.Log(Resources.LogOverwritingOldFiles);

                    var prefix = Path.Combine(_tempDir, "contents");
                    var queue = new List<DirectoryInfo>() { new DirectoryInfo(prefix) };
                    while (queue.Count() > 0)
                    {
                        var info = queue[0];
                        queue.RemoveAt(0);

                        var sub = info.FullName.Substring(prefix.Length).TrimStart('\\', '/');
                        var sub_destDir = Path.Combine(_destDir, sub);
                        if (!Directory.Exists(sub_destDir))
                        {
                            Directory.CreateDirectory(sub_destDir);
                        }

                        foreach (var item in info.EnumerateDirectories())
                        {
                            queue.Add(item);
                        }

                        foreach (var item in info.EnumerateFiles())
                        {
                            File.Delete(Path.Combine(sub_destDir, item.Name));
                            File.Move(item.FullName, Path.Combine(sub_destDir, item.Name));
                        }
                    }
                }
                catch (Exception e)
                {
                    _display.Log(string.Format(Resources.LogOverwritingOldFilesFailed, e));
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                _display.Log(string.Format(Resources.Exception, e));
                return false;
            }
        }

        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
            {
                _display.Log(Resources.LogDeletingTempFiles);

                try
                {
                    Directory.Delete(_tempDir, true);
                    _display.Log(Resources.LogDone);
                }
                catch (Exception ex)
                {
                    _display.Log(string.Format(Resources.LogFailedToDelete, _tempDir, ex));
                }
            }
        }
    }
}
