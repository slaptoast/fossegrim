using System.Collections.Concurrent;
using Fossegrim.Lib.Enums;
using Fossegrim.Lib.Models;
using TagLib;

namespace Fossegrim.Lib.Services;

/// <summary>
/// Scans all files in a directory and its subdirectories using multithreading
/// </summary>
public class FileScanner
{
    private readonly object _consoleLock = new object();
    private readonly int _maxDegreeOfParallelism;
    private ConcurrentBag<MediaItem> items;

    /// <summary>
    /// Creates a new FileScanner instance
    /// </summary>
    /// <param name="maxDegreeOfParallelism">Maximum number of concurrent threads (default: processor count)</param>
    public FileScanner(int maxDegreeOfParallelism = -1)
    {
        items = new ConcurrentBag<MediaItem>();
        _maxDegreeOfParallelism = maxDegreeOfParallelism == -1
            ? Environment.ProcessorCount
            : maxDegreeOfParallelism;
    }

    /// <summary>
    /// Asynchronously scans all files in the specified directory and its subdirectories
    /// </summary>
    /// <param name="folderPath">The root folder path to scan</param>
    public async Task<IEnumerable<MediaItem>> ScanFilesAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path cannot be null or empty", nameof(folderPath));
        }

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {folderPath}");
        }

        return await ScanDirectoryAsync(folderPath);
    }

    private async Task<IEnumerable<MediaItem>> ScanDirectoryAsync(string directoryPath)
    {
        try
        {
            // Get and output all files in the current directory
            string[] files = Directory.GetFiles(directoryPath);
            foreach (string file in files)
            {
                MediaFileType filetype = MediaFileTypeHelper.GetFileType(file.Split(".").Last());
                if (MediaFileTypeHelper.IsAudio(filetype))
                {
                    if (filetype == MediaFileType.MP3)
                    {
                        try
                        {
                            // Use TagLib# to read tags
                            using (var tagFile = TagLib.File.Create(file))
                            {
                                if (tagFile.Tag != null)
                                {
                                    items.Add(new MediaItem
                                    {
                                        Album = tagFile.Tag.Album,
                                        ArtistName = tagFile.Tag.FirstPerformer ?? string.Join(", ", tagFile.Tag.Performers ?? Array.Empty<string>()),
                                        Title = tagFile.Tag.Title,
                                        FileLocation = file
                                    });
                                }
                                else
                                {
                                    WriteToConsole($"No tags found: {file}");
                                }
                            }
                        }
                        catch (CorruptFileException ex)
                        {
                            WriteToConsole($"Corrupt file: {file} - {ex.Message}");
                        }
                        catch (UnsupportedFormatException ex)
                        {
                            WriteToConsole($"Unsupported format: {file} - {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            WriteToConsole($"Error reading file: {file} - {ex.Message}");
                        }
                    }
                }
            }

            // Scan subdirectories in parallel asynchronously with controlled parallelism
            string[] subdirectories = Directory.GetDirectories(directoryPath);

            // Use SemaphoreSlim to limit concurrent directory scans
            using (var semaphore = new SemaphoreSlim(_maxDegreeOfParallelism))
            {
                var tasks = subdirectories.Select(async subdirectory =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        await ScanDirectoryAsync(subdirectory);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }

            return items;
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteToConsole($"Access denied to directory: {directoryPath} - {ex.Message}");
        }
        catch (Exception ex)
        {
            WriteToConsole($"Error scanning directory {directoryPath}: {ex.Message}");
        }
        return items;
    }

    private void WriteToConsole(string message)
    {
        lock (_consoleLock)
        {
            Console.WriteLine(message);
        }
    }
}
