using TagLib;

public static class TestFile
{
    public static void TestSpecificFile()
    {
        string filePath = "/Users/tj/Documents/Music/Jose Gonzales/In Our Nature/05 Teardrop.mp3";

        Console.WriteLine($"Testing file: {filePath}");
        Console.WriteLine($"File exists: {System.IO.File.Exists(filePath)}");
        Console.WriteLine($"File size: {new FileInfo(filePath).Length} bytes");
        Console.WriteLine();

        try
        {
            Console.WriteLine("Opening file with TagLib#...");
            using (var tagFile = TagLib.File.Create(filePath))
            {
                Console.WriteLine("File opened successfully");
                Console.WriteLine();

                // Audio properties
                if (tagFile.Properties != null)
                {
                    Console.WriteLine("Audio Properties:");
                    Console.WriteLine($"  Duration: {tagFile.Properties.Duration}");
                    Console.WriteLine($"  Bitrate: {tagFile.Properties.AudioBitrate} kbps");
                    Console.WriteLine($"  Sample Rate: {tagFile.Properties.AudioSampleRate} Hz");
                    Console.WriteLine($"  Channels: {tagFile.Properties.AudioChannels}");
                    Console.WriteLine();
                }

                // Tag information
                if (tagFile.Tag != null)
                {
                    Console.WriteLine("Tag Information:");
                    Console.WriteLine($"  Title: '{tagFile.Tag.Title}'");
                    Console.WriteLine($"  Album: '{tagFile.Tag.Album}'");
                    Console.WriteLine($"  First Performer: '{tagFile.Tag.FirstPerformer}'");
                    Console.WriteLine($"  All Performers: {string.Join(", ", tagFile.Tag.Performers ?? Array.Empty<string>())}");
                    Console.WriteLine($"  Year: {tagFile.Tag.Year}");
                    Console.WriteLine($"  Track: {tagFile.Tag.Track}");
                    Console.WriteLine($"  Genre: {string.Join(", ", tagFile.Tag.Genres ?? Array.Empty<string>())}");
                    Console.WriteLine();

                    Console.WriteLine($"SUCCESS: Tags read successfully!");
                }
                else
                {
                    Console.WriteLine("WARNING: No tags found in file");
                }

                // Tag types
                Console.WriteLine();
                Console.WriteLine("Tag Types Present:");
                Console.WriteLine($"  Tag Types: {tagFile.TagTypes}");
                Console.WriteLine($"  Tag Types as Flags: {tagFile.TagTypesOnDisk}");

                // Check for specific tag types
                if ((tagFile.TagTypesOnDisk & TagTypes.Id3v1) != 0)
                    Console.WriteLine("  - ID3v1 tag present");
                if ((tagFile.TagTypesOnDisk & TagTypes.Id3v2) != 0)
                    Console.WriteLine("  - ID3v2 tag present");
                if ((tagFile.TagTypesOnDisk & TagTypes.Ape) != 0)
                    Console.WriteLine("  - APE tag present");

                Console.WriteLine();
                Console.WriteLine("Checking first 10 bytes of file:");
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    byte[] buffer = new byte[10];
                    fs.ReadExactly(buffer, 0, 10);
                    Console.WriteLine($"  Hex: {BitConverter.ToString(buffer)}");
                    Console.WriteLine($"  String: {System.Text.Encoding.ASCII.GetString(buffer)}");
                }
            }
        }
        catch (CorruptFileException ex)
        {
            Console.WriteLine($"ERROR: Corrupt file - {ex.Message}");
        }
        catch (UnsupportedFormatException ex)
        {
            Console.WriteLine($"ERROR: Unsupported format - {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                Console.WriteLine($"Inner stack: {ex.InnerException.StackTrace}");
            }
        }
    }
}
