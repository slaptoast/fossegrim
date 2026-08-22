using System.Runtime.CompilerServices;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;

Console.WriteLine("Fossegrim Console Application");
Console.WriteLine("=============================");
Console.WriteLine();

// Test the specific file first
//Console.WriteLine("Testing specific file...");
//TestFile.TestSpecificFile();
Console.WriteLine();

string folderPath = "/Users/tj/Documents/Music";

try
{
    var scanner = new FileScanner();

    var items = await scanner.ScanFilesAsync(folderPath);

    Console.WriteLine("Total Items:{0}", items.Count());
    // foreach (MediaItem item in items)
    // {
    //     if (item != null)
    //     {
    //         Console.WriteLine("Title: {0} - Album: {1} - Artist: {2} - Filename: {3}", item.Title, item.Album, item.ArtistName, item.FileLocation);
    //     }
    // }

    Console.WriteLine();
    Console.WriteLine("Scan completed successfully!");
}
catch (Exception ex)
{
    Console.WriteLine($"Error during scan: {ex.Message}");
}
