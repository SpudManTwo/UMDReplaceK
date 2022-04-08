using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScriptFileMapper
{
    static class UMDReplace
    {
        //Version Constants
        const string VERSION = "2020105K";

        //Int Constants
        const short DESCRIPTOR_LBA = 16; // 16 = 0x010 = LBA of the first volume descriptor
        const short TOTAL_SECTORS = 80; // 80 = 0x050 = total number of sectors in the UMD
        const short ROOT_FOLDER_LBA = 158; // 158 = 0x09E LBA of the root folder
        const short ROOT_SIZE = 166; // 166 = 0x0A6 size of the root folder
        const short TABLE_PATH_LEN = 132; // 132 = 0x084 size of the first table path
        const short TABLE_PATH_LBA = 140; // 132 = 0x08C LBA of the first table path

        //Long Constants
        const ulong DESCRIPTOR_SIG_1 = 335558014681857; // 335558014681857 = 0x0001313030444301ULL volume descriptor signatures
        const ulong DESCRIPTOR_SIG_2 = 335558014682111; // 335558014681857 = 0x00013130304443FFULL

        //M0 Constants
        const uint sectorSize = 2048;// 2048 = 0x800 = LEN_SECTOR_M0 = sector size
        const uint sectorData = 2048; // 2048 = 0x800 = POS_DATA_M0 = sector data length
        const uint dataOffset = 0; // 0 = 0x000 = LEN_DATA_M0 = sector data start

        //File Input/Output Constants
        const string TMPNAME = "umd-replacek.$"; // temporal name
        const int BLOCKSIZE = 16384; // sectors to read/write at once

        //Non-Constant Ints
        static ushort mode;        // image mode

        //Exit Code Definitions as per https://docs.microsoft.com/en-us/windows/win32/debug/system-error-codes
        const int SUCCESS_EXIT_CODE = 0;
        const int BAD_APPLICATION_ARGUMENTS_EXIT_CODE = 160;
        const int FILE_NOT_FOUND_EXIT_CODE = 2;
        const int FILE_NAME_TOO_LONG = 111;
        const int CANNOT_OPEN_FILE_EXIT_CODE = 110;

        static bool fileLock = false;

        static long SearchTime = 0;
        static long PathTableTime = 0;
        static long TOCTime = 0;
        static long ExchangeTime = 0;
        static long ConversionTime = 0;
        static byte[] originalIsoFile;
        static byte[] newIsoFile;
        static Dictionary<string, ulong> filesForReplacement = new Dictionary<string, ulong>();
        static Dictionary<string, uint> oldFileSizes = new Dictionary<string, uint>();
        static Dictionary<string, uint> oldFileSectorCounts = new Dictionary<string, uint>();
        static Dictionary<string, uint> newFileSizes = new Dictionary<string, uint>();
        static Dictionary<string, uint> newFileSectorCounts = new Dictionary<string, uint>();
        static Dictionary<string, byte[]> newFileSectors = new Dictionary<string, byte[]>();
        static uint rootLba = 0;
        static uint rootLength = 0;
        static Random rngGenerator = new Random();

        static async Task Main(string[] args)
        {
            //Title
            System.Console.WriteLine(
                @$"
UMD-Replace K version {VERSION} - is a C# feature enriched successor of the original UMD-Replace 20150303 Copyright (C) 2012-2015 CUE
Tiny tool to replace data files in a PSP UMD ISO.
UMD-Replace K written and provided by @SpudManTwo and @Dormanil
");

            if (args.Length == 2)
            {
                //Read in batch set of arguments.
                List<string> batchArgs = new List<string>() { args[0] };
                using (StreamReader reader = new StreamReader(new FileStream(args[1], FileMode.Open)))
                {
                    while (!reader.EndOfStream)
                        batchArgs.Add(reader.ReadLine());
                }
                args = batchArgs.ToArray();
            }

            //Arg Check
            if (args.Length % 2 != 1)
            {
                //Usage Explanation
                System.Console.WriteLine(@"Usage: UMD-REPLACEK imagename {batchFileOfArguments | (filename newfile ...)}
- 'imagename' is the name of the UMD image
- 'batchFileOfArguments' is the file where a set of batch arguments are stored. This is used for replacing files in a larger bulk than command line arguments can support
- 'filename' is the file in the UMD image with the data to be replaced
- 'newfile' is the relative file path with the new data
* 'imagename' must be a valid UMD ISO image
* 'filename' can use either the slash or backslash
* 'newfile' can be different size as 'filename'");
                //Bad Argument Exit
                Environment.Exit(BAD_APPLICATION_ARGUMENTS_EXIT_CODE);
            }

            // Prepare file for reading into buffer
            GetFileReadStream(args[0].Replace("\"", ""), out FileStream inputFileStream);

            // Prepare buffer
            originalIsoFile = new byte[inputFileStream.Length];

            // Read file into Buffer
            try
            {
                inputFileStream.Read(originalIsoFile);
                inputFileStream.Flush();
            }
            finally
            {
                inputFileStream.Dispose();
            }

            bool wasChanged = false;
            long diff = 0;
            rootLba = BitConverter.ToUInt32(originalIsoFile[(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + ROOT_FOLDER_LBA)..(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + ROOT_FOLDER_LBA + 4)]);
            rootLength = BitConverter.ToUInt32(originalIsoFile[(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + ROOT_SIZE)..(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + ROOT_SIZE + 4)]);


            //Create a queue of search tasks to find all the files in the iso.
            Task[] oldFilePrepQueue = new Task[args.Length/2];
            Task[] newFilePrepQueue = new Task[args.Length / 2];

            //Locate all the old files while also preparing the new files for insertion.
            for (int i = 1; i < args.Length; i += 2)
            {
                //Run the Replace
                System.Console.WriteLine("- finding file " + args[i].Replace("\"", ""));
                string oldName = args[i].Replace("\"", "");
                if ((oldName[0] != '/') && (oldName[0] != '\\'))
                {
                    oldName = '/' + oldName;
                }

                // change all backslashes by slashes in 'oldname'
                oldName = oldName.Replace("\\", "/");
                oldFilePrepQueue[i/2] = PrepareOldFileForReplacement(oldName);
                newFilePrepQueue[i/2] = PrepareNewFileForInsertion(args[i+1].Replace("\"", ""));
            }

            Task.WaitAll(oldFilePrepQueue);
            Task.WaitAll(newFilePrepQueue);

            filesForReplacement = filesForReplacement.OrderBy(fileNameLocation => fileNameLocation.Value).ToDictionary(pair => pair.Key, pair => pair.Value);

            newIsoFile = new byte[newFileSectorCounts.Sum(pair => pair.Value)*sectorSize];

            for (int i = 1; i < args.Length; i += 2)
            {
                int originalSize = originalIsoFile.Length;
                //Run the Replace
                Console.WriteLine("- replacing file " + args[i].Replace("\"", ""));
                originalIsoFile = await Replace(args[i].Replace("\"", ""), args[i + 1].Replace("\"", ""));
                diff += originalIsoFile.Length - originalSize;
                if (diff != 0)
                {
                    wasChanged = true;
                }
            }

            //Delete the old file now that we've got all that we need.
            System.IO.File.Delete(args[0]);

            //Write out the new file.
            GetFileWriteStream(args[0], out FileStream outputStream);

            // Write file from Buffer
            try
            {
                //Writing new iso file
                using (BinaryWriter binaryWriter = new BinaryWriter(outputStream))
                {
                    binaryWriter.Write(originalIsoFile);
                }
            }
            finally
            {
                outputStream.Dispose();
            }

            System.Console.Write("- the new image has ");
            if (diff > 0)
            {
                System.Console.WriteLine(diff + " more bytes than the original image.");
            }
            else if (diff < 0)
            {
                System.Console.WriteLine(diff + " less bytes than the original image.");
            }
            else
            {
                System.Console.WriteLine("the same amount of bytes as the original image.");
            }
            if (wasChanged)
            {
                System.Console.WriteLine("- maybe you need to hand update the cuesheet file (if exist and needed)");
            }

            System.Console.WriteLine("\nDone");

            //Successful Exit
            Environment.Exit(SUCCESS_EXIT_CODE);
        }

        static async Task<byte[]> Replace(string oldName, string newName)
        {
            // get data from the primary volume descriptor
            uint imageSectors = BitConverter.ToUInt32(originalIsoFile[(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + TOTAL_SECTORS)..(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + TOTAL_SECTORS + 4)]);
            long pos = sectorSize * DESCRIPTOR_LBA;
            
            // get new data from the new file
            uint newFileSize = newFileSizes[newName];
            uint newFileSectorCount = newFileSectorCounts[newName];

            // 'oldName' must start with a path separator
            if ((oldName[0] != '/') && (oldName[0] != '\\'))
            {
                oldName = '/' + oldName;
            }

            // change all backslashes by slashes in 'oldname'
            oldName = oldName.Replace("\\", "/");

            if(!filesForReplacement.ContainsKey(oldName))
                Environment.Exit(FILE_NOT_FOUND_EXIT_CODE);
            ulong foundPosition = filesForReplacement[oldName];
            uint foundLBA = (uint)(foundPosition / sectorSize);
            uint foundOffset = (uint)(foundPosition % sectorSize);

            // get data from the old file as already calculated
            uint oldFileSize = oldFileSizes[oldName]; //Since we've already calculated this, just pull the values
            uint oldFileSectorCount = oldFileSectorCounts[oldName];

            //Calculate the LBA
            uint fileLBA = BitConverter.ToUInt32(originalIsoFile[(int)(sectorSize * foundLBA + foundOffset + 2)..(int)(sectorSize * foundLBA + foundOffset + 6)]); //0x02 = 2

            //size difference in sectors
            int diff = BitConverter.ToInt32(BitConverter.GetBytes(newFileSectorCount - oldFileSectorCount));

            //As a change from the original C code, we won't be creating a duplicate file just for reading as we update since we're storing the whole file in memory.
            //That said, I have left the equivalent C# code commented out below if you want that functionality brought back for some reason.
            uint lba = fileLBA;

            /*
            if (diff != 0)
            {
                //create the new image
                System.Console.WriteLine("- creating temporal image");
                FileStream outputStream = File.Create(name);
                uint lba = 0;
                //update the previous sectors
                System.Console.WriteLine("- updating previous data sectors");
                uint maxim = fileLBA;
                uint count = 0;
                for(uint i=0;i < fileLBA; i += count, lba += count)
                {
                    count = maxim >= BLOCKSIZE ? BLOCKSIZE : maxim; maxim -= count;
                    using (BinaryWriter writer = new BinaryWriter(outputStream))
                    {
                        writer.Write(originalIsoFile.GetRange((int)i, (int)count).ToArray());
                    }
                }
            }
            else 
            {
                lba = fileLBA;
            }
            */

            // update the new file

            if (newFileSectorCount != 0)
            {
                //Inject the new sectors in to the place where the old ones used to sit.
                ExchangeSectors((int)(lba++), (int)oldFileSectorCount, newFileSectors[newName]);
            }

            if (newFileSize != oldFileSize)
            {
                // update the file size
                System.Console.WriteLine("- updating file size");
                uint littleEndian = newFileSize;
                uint bigEndian = ChangeEndian(littleEndian);
                byte[] littleEndianBytes = BitConverter.GetBytes(littleEndian);
                byte[] bigEndianBytes = BitConverter.GetBytes(bigEndian);
                for (int i = 0; i < 4; i++)
                {
                    //Replace the old little endian bytes
                    originalIsoFile[(int)(sectorSize * foundLBA + foundOffset + 10 + i)] = littleEndianBytes[i]; //0x0A = 10
                    //Replace the old big endian bytes
                    originalIsoFile[(int)(sectorSize * foundLBA + foundOffset + 14 + i)] = bigEndianBytes[i]; //0x0E = 14 
                }
            }

            if (diff != 0)
            {
                // update the primary volume descriptor
                System.Console.WriteLine("- updating primary volume descriptor");
                uint littleEndian = (uint)(imageSectors + diff);
                uint bigEndian = ChangeEndian(littleEndian);
                byte[] littleEndianBytes = BitConverter.GetBytes(littleEndian);
                byte[] bigEndianBytes = BitConverter.GetBytes(bigEndian);
                for (int i = 0; i < 4; i++)
                {
                    //Replace the old little endian bytes
                    originalIsoFile[(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + TOTAL_SECTORS + i)] = littleEndianBytes[i]; //0x0A = 10
                    //Replace the old big endian bytes
                    originalIsoFile[(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + TOTAL_SECTORS + 4 + i)] = bigEndianBytes[i]; //0x0E = 14 
                }

                // update the path tables
                System.Console.WriteLine("- updating path tables");
                //Unsure about this next block
                for (int i = 0; i < 4; i++)
                {
                    uint tblLen = BitConverter.ToUInt32(originalIsoFile[(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + TABLE_PATH_LEN)..(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + TABLE_PATH_LEN + 4)]);
                    uint tblLBA = BitConverter.ToUInt32(originalIsoFile[(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + TABLE_PATH_LBA + 4 * i)..(int)(sectorSize * DESCRIPTOR_LBA + dataOffset + TABLE_PATH_LBA + 4 * i + 4)]);
                    if (tblLBA != 0)
                    {
                        if (i == 2) //0x2 = 2 Bit wise & for byte comparison in C.
                        {
                            tblLBA = ChangeEndian(tblLBA);
                        }
                        PathTable(originalIsoFile, tblLBA, tblLen, fileLBA, diff, i == 2); //0x2 = 2
                    }
                }

                // update the file/folder LBAs
                System.Console.WriteLine("- updating entire TOCs");

                TOC(originalIsoFile, rootLba, rootLength, foundPosition, fileLBA, diff);
            }

            return originalIsoFile;
        }

        private static void GetFileReadStream(string filePath, out FileStream fs)
        {
            try
            {
                fs = File.Open(filePath, FileMode.Open);
                return;
            }
            catch (ArgumentException)
            {
                System.Console.WriteLine($"No such input file for '{filePath}'\n");
                Environment.Exit(FILE_NOT_FOUND_EXIT_CODE);
            }
            catch (PathTooLongException)
            {
                System.Console.WriteLine($"Input File Path '{filePath}' exceeds system-defined length.\n");
                Environment.Exit(FILE_NAME_TOO_LONG);
            }
            catch (DirectoryNotFoundException)
            {
                System.Console.WriteLine($"Specified input file path'{filePath}' is invalid.\n");
                Environment.Exit(FILE_NOT_FOUND_EXIT_CODE);
            }
            catch (UnauthorizedAccessException)
            {
                System.Console.WriteLine($"Current User lacks sufficient permissions for input file '{filePath}'\n");
                Environment.Exit(CANNOT_OPEN_FILE_EXIT_CODE);
            }
            catch (FileNotFoundException)
            {
                System.Console.WriteLine($"No such input file '{filePath}'\n");
                Environment.Exit(FILE_NOT_FOUND_EXIT_CODE);
            }
            catch (NotSupportedException)
            {
                System.Console.WriteLine($"Input File '{filePath}' is not in a recognizable format.\n");
                Environment.Exit(CANNOT_OPEN_FILE_EXIT_CODE);
            }
            catch (IOException)
            {
                System.Console.WriteLine($"A error occured while opening the input file '{filePath}'\n");
                Environment.Exit(CANNOT_OPEN_FILE_EXIT_CODE);
            }

            fs = default;
        }

        private static void GetFileWriteStream(string filePath, out FileStream fs)
        {
            try
            {
                fs = File.Open(filePath, FileMode.Create);
                return;
            }
            catch (ArgumentException)
            {
                System.Console.WriteLine("No such iso file '" + filePath + "'\n");
                Environment.Exit(FILE_NOT_FOUND_EXIT_CODE);
            }
            catch (PathTooLongException)
            {
                System.Console.WriteLine("Iso File Path '" + filePath + "' exceeds system-defined length.\n");
                Environment.Exit(FILE_NAME_TOO_LONG);
            }
            catch (DirectoryNotFoundException)
            {
                System.Console.WriteLine("Specified iso path'" + filePath + "' is invalid.\n");
                Environment.Exit(FILE_NOT_FOUND_EXIT_CODE);
            }
            catch (UnauthorizedAccessException)
            {
                System.Console.WriteLine("Current User lacks sufficient permissions for iso file '" + filePath + "'\n");
                Environment.Exit(CANNOT_OPEN_FILE_EXIT_CODE);
            }
            catch (FileNotFoundException)
            {
                System.Console.WriteLine("No such iso file '" + filePath + "'\n");
                Environment.Exit(FILE_NOT_FOUND_EXIT_CODE);
            }
            catch (NotSupportedException)
            {
                System.Console.WriteLine("Iso File '" + filePath + "' is not in a recognizable format.\n");
                Environment.Exit(CANNOT_OPEN_FILE_EXIT_CODE);
            }
            catch (IOException)
            {
                System.Console.WriteLine("A error occured while opening the iso file '" + filePath + "'\n");
                Environment.Exit(CANNOT_OPEN_FILE_EXIT_CODE);
            }
            fs = default;
        }

        static async Task<ulong> Search(string fileName, string path, uint lba, uint len)
        {
            long start = DateTime.Now.Ticks;
            ulong totalSectors = (ulong)((len + sectorSize - 1) / sectorSize);
            for (uint i = 0; i < totalSectors; i++)
            {
                uint nBytes;
                for (long position = 0; position < sectorData && (dataOffset + position + 4) < originalIsoFile.Length; position += nBytes)
                {
                    if (sectorSize * (lba + i) + dataOffset + position >= originalIsoFile.Length)
                    {
                        break;
                    }
                    //field size
                    nBytes = originalIsoFile[(int)(sectorSize * (lba + i) + dataOffset + position)];
                    if (nBytes == 0)
                    {
                        break;
                    }

                    //name size
                    byte nChars = originalIsoFile[(int)(sectorSize * (lba + i) + dataOffset + position + 32)]; //0x020 = 32
                    byte[] name = new byte[nChars];
                    originalIsoFile[(int)(sectorSize * (lba + i) + dataOffset + position + 33)..(int)(sectorSize * (lba + i) + dataOffset + position + 33+ nChars)].CopyTo(name, 0);

                    // discard the ";1" final
                    if (nChars > 2 && name[nChars - 2] == 59) //0x3B = 59 = ';' in ASCII
                    {
                        nChars -= 2;
                        name[nChars] = 0;
                    }

                    string nameString = Encoding.ASCII.GetString(name);

                    // check the name except for '.' and '..' entries
                    if ((nChars != 1) || ((name[0] != 0) && (name[0] != 1))) // 0x1 = Repeat previous character in ASCII
                    {
                        //new path name
                        string newPath = $"{path}/{nameString}"; // sprintf is the string format for C. While the syntax looks different, the functionality is supposed to be the same.
                        if (originalIsoFile[(int)(sectorSize * (lba + i) + dataOffset + position + 25)] == 2) // 0x002 = 2
                        {
                            //Recursive Search Through Folders

                            // 0x019 = 25, Bitwise & in C compares two bytes for equality.
                            uint newLBA = BitConverter.ToUInt32(originalIsoFile[(int)(sectorSize * (lba + i) + dataOffset + position + 2)..(int)(sectorSize * (lba + i) + dataOffset + position + 6)]); // 0x002 = 2
                            uint newLen = BitConverter.ToUInt32(originalIsoFile[(int)(sectorSize * (lba + i) + dataOffset + position + 10)..(int)(sectorSize * (lba + i) + dataOffset + position + 14)]); // 0x00A = 10

                            ulong found = await Search(fileName, newPath, newLBA, newLen);

                            if (found != 0)
                            {
                                return found;
                            }
                        }
                        // compare names - case insensitive
                        else if (fileName.Equals(newPath, StringComparison.InvariantCultureIgnoreCase))
                        {
                            // file found
                            SearchTime += DateTime.Now.Ticks - start;
                            if (!filesForReplacement.ContainsKey(fileName))
                                filesForReplacement.Add(fileName, (ulong)((lba + i) * sectorSize + dataOffset + position));
                            return (ulong)((lba + i) * sectorSize + dataOffset + position);
                        }
                    }
                }
            }

            //if not found return 0
            return 0;
        }

        static void PathTable(Span<byte> originalIso, uint lba, uint len, uint lbaOld, int diff, bool sw)
        {
            long start = DateTime.Now.Ticks;

            uint nBytes;

            for (uint pos = 0; pos < len; pos += 8 + nBytes + (nBytes & 0x1)) //0x01 = 1 Oh and this one is actually used as a bit-wise & so I'm leaving it in hex
            {
                if (sectorSize * lba + dataOffset + pos >= originalIso.Length)
                {
                    break;
                }
                //field size
                nBytes = originalIso[(int)(sectorSize * lba + dataOffset + pos)];
                if (nBytes == 0)
                {
                    break;
                }

                //position
                uint newLBA = BitConverter.ToUInt32(originalIso.Slice((int)(sectorSize * lba + dataOffset + pos + 2), 4)); //0x002 = 2
                if (sw)
                {
                    newLBA = ChangeEndian(newLBA);
                }

                //update needed
                if (newLBA > lbaOld)
                {
                    newLBA += (uint)diff;
                    if (sw)
                    {
                        newLBA = ChangeEndian(newLBA);
                    }

                    byte[] newLBABytes = BitConverter.GetBytes(newLBA);

                    //update sectors if needed, 0x002 = 2
                    for (int i = 0; i < 4; i++)
                    {
                        originalIso[(int)(sectorSize * lba + dataOffset + pos + 2 + i)] = newLBABytes[i];
                    }
                }
            }
            
            PathTableTime += DateTime.Now.Ticks - start;

        }

        static void TOC(Span<byte> originalIso, uint lba, uint len, ulong found, uint lbaOld, int diff)
        {
            long start = DateTime.Now.Ticks;

            // total sectors
            long totalSectors = (len + sectorSize - 1) / sectorSize;

            for (uint i = 0; i < totalSectors; i++)
            {
                uint nBytes;
                for (uint pos = 0; pos < len; pos += nBytes)
                {
                    if (sectorSize * (lba + i) + dataOffset + pos >= originalIso.Length)
                    {
                        break;
                    }
                    //field size
                    nBytes = originalIso[(int)(sectorSize * (lba + i) + dataOffset + pos)];
                    if (nBytes == 0)
                    {
                        break;
                    }

                    //name size
                    byte nChars = originalIso[(int)(sectorSize * (lba + i) + dataOffset + pos + 32)]; //0x020 = 32
                    Span<byte> name = new Span<byte>(new byte[nChars]);
                    originalIso.Slice((int)(sectorSize * (lba + i) + dataOffset + pos + 33), nChars).CopyTo(name);
                    string nameString = Encoding.ASCII.GetString(name);

                    // position
                    uint newLBA = BitConverter.ToUInt32(originalIso.Slice((int)(sectorSize * (lba + i) + dataOffset + pos + 2), 4)); // 0x002 = 2

                    // needed to change a 0-bytes file with more 0-bytes files (same LBA)
                    ulong newfound = (ulong)(lba + i) * sectorSize + dataOffset + pos;

                    if ((newLBA > lbaOld) || ((newLBA == lbaOld) && (newfound > found)))
                    {
                        //update sector if needed
                        newLBA += (uint)diff;

                        byte[] newLBABytes = BitConverter.GetBytes(newLBA);
                        byte[] bigEndianBytes = BitConverter.GetBytes(ChangeEndian(newLBA));

                        for (int endianByte = 0; endianByte < 4; endianByte++)
                        {
                            originalIso[(int)(sectorSize * (lba + i) + dataOffset + pos + 2 + endianByte)] = newLBABytes[endianByte]; // 0x002 = 2
                            originalIso[(int)(sectorSize * (lba + i) + dataOffset + pos + 6 + endianByte)] = bigEndianBytes[endianByte]; // 0x006 = 6
                        }
                    }

                    // check the name except for '.' and '..' entries
                    if ((nChars != 1) || ((name[0] != 0) && (name[0] != 1))) // 0x0 = 0 and 0x1 = repeat previous character in ASCII
                    {
                        //recursive update in folders
                        if (originalIso[(int)(sectorSize * (lba + i) + dataOffset + pos + 25)] == 2) //0x02 = 2
                        {
                            uint newLen = BitConverter.ToUInt32(originalIso.Slice((int)(sectorSize * (lba + i) + dataOffset + pos + 10), 4)); // 0x00A = 10

                            TOC(originalIso, newLBA, newLen, found, lbaOld, diff);
                        }
                    }
                }
            }
            TOCTime += DateTime.Now.Ticks - start;
        }

        static uint ChangeEndian(uint value)
        {
            return BitConverter.ToUInt32(BitConverter.GetBytes(value).Reverse().ToArray());
        }

        static void ExchangeSectors(int offset, int originalFileSectors, in byte[] dataToExchange)
        {
            while(fileLock) 
            {
                Thread.Sleep(rngGenerator.Next(0, 10));
                //Wait for file to be unlocked
            }
            fileLock = true;
            long start = DateTime.Now.Ticks;
            originalIsoFile = 
                        originalIsoFile[0..(int)(offset * sectorSize)]
                            .Concat(dataToExchange)
                            .Concat(originalIsoFile[(int)(sectorSize * (offset + originalFileSectors))..^0])
                            .ToArray();
            ExchangeTime += DateTime.Now.Ticks - start;
            fileLock = false;
        }

        static async Task<byte[]> ConvertToSectors(byte[] fileData)
        {
            long start = DateTime.Now.Ticks;
            if (fileData.Length % sectorSize == 0)
            {
                ConversionTime += DateTime.Now.Ticks - start;
                return fileData.ToArray();
            }
            byte[] bytePadding = new byte[sectorSize - fileData.Length % sectorSize];
            Array.Fill<byte>(bytePadding, 0);
            ConversionTime += DateTime.Now.Ticks - start;
            return fileData.ToArray().Concat(bytePadding).ToArray();
        }

        static async Task PrepareNewFileForInsertion(string newFileName)
        {
            GetFileReadStream(newFileName, out FileStream inputFileStream);

            // Prepare buffer
            byte[] newFileBytes = new byte[inputFileStream.Length];

            try
            {
                _ = inputFileStream.Read(newFileBytes);
            }
            finally
            {
                inputFileStream.Dispose();
            }

            // get new data from the new file

            uint newFileSize = (uint)newFileBytes.Length;
            newFileSizes.Add(newFileName, newFileSize);
            uint newFileSectorCount = (uint)((newFileSize + sectorData - 1) / sectorData);
            newFileSectorCounts.Add(newFileName, newFileSectorCount);

            if(newFileSectorCount != 0)
                newFileSectors.Add(newFileName, await ConvertToSectors(newFileBytes));
        }
        
        static async Task PrepareOldFileForReplacement(string normalizedOldFileName)
        {
            ulong foundPosition = await Search(normalizedOldFileName, string.Empty, rootLba, rootLength);
            uint foundLBA = (uint)(foundPosition / sectorSize);
            uint foundOffset = (uint)(foundPosition % sectorSize);
            uint oldFileSize = BitConverter.ToUInt32(originalIsoFile[(int)(sectorSize * foundLBA + foundOffset + 10)..(int)(sectorSize * foundLBA + foundOffset + 14)]);
            oldFileSizes.Add(normalizedOldFileName, oldFileSize);
            oldFileSectorCounts.Add(normalizedOldFileName, (oldFileSize + sectorData - 1) / sectorData);
        }
    }
}