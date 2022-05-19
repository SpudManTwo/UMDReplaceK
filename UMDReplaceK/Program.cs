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
        const string VERSION = "20220519K";

        //Int Constants
        const short DESCRIPTOR_LBA = 16; // 16 = 0x010 = LBA of the first volume descriptor
        const short TOTAL_SECTORS = 80; // 80 = 0x050 = total number of sectors in the UMD
        const short TABLE_PATH_LBA = 140; // 132 = 0x08C LBA of the first table path

        //M0 Constants
        const long sectorSize = 2048;// 2048 = 0x800 = LEN_SECTOR_M0 = sector size
        const uint sectorData = 2048; // 2048 = 0x800 = POS_DATA_M0 = sector data length
        const uint dataOffset = 0; // 0 = 0x000 = LEN_DATA_M0 = sector data start

        //File Input/Output Constants
        const string TMPNAME = "umd-replacek.$"; // temporal name

        //Non-Constant Ints
        static ushort mode;        // image mode

        //Exit Code Definitions as per https://docs.microsoft.com/en-us/windows/win32/debug/system-error-codes
        const int SUCCESS_EXIT_CODE = 0;
        const int BAD_APPLICATION_ARGUMENTS_EXIT_CODE = 160;
        const int FILE_NOT_FOUND_EXIT_CODE = 2;
        const int FILE_NAME_TOO_LONG = 111;
        const int CANNOT_OPEN_FILE_EXIT_CODE = 110;

        static bool fileLock = false;

        static byte[][] originalIsoFile;
        static byte[][] newIsoFile;
        static Dictionary<string, ulong> filesForReplacement = new Dictionary<string, ulong>();
        static Dictionary<string, uint> oldFileSizes = new Dictionary<string, uint>();
        static Dictionary<string, uint> oldFileSectorCounts = new Dictionary<string, uint>();
        static Dictionary<string, uint> newFileSizes = new Dictionary<string, uint>();
        static Dictionary<string, uint> newFileSectorCounts = new Dictionary<string, uint>();
        static Dictionary<string, byte[]> newFileSectors = new Dictionary<string, byte[]>();
        static uint rootLba = 0;
        static uint rootLength = 0;
        static Random rngGenerator = new Random();
        static long originalIsoFileLength;

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

            originalIsoFileLength = inputFileStream.Length;

            // Prepare buffer
            if (inputFileStream.Length > Array.MaxLength) //Fun fact for the C# curious, Array.MaxLength > int.MaxValue at the time of writing, so we need to use Array.MaxLength specifically
			{
                //If the ROM is bigger than a singular array supports, we use multiple arrays.
                //This is for PS2 support as requested by IlDucci as this tool works for both PSP and PS2 apparently.
                originalIsoFile = new byte[(inputFileStream.Length / Array.MaxLength)+1][];
                for(int i=0;i<originalIsoFile.Length-1;i++)
                    originalIsoFile[i] = new byte[Array.MaxLength];
                originalIsoFile[^1] = new byte[inputFileStream.Length % Array.MaxLength];
            }
            else
			{
                //This is the tried and true, singular array support that we're used to PSP.
                originalIsoFile = new byte[][]
                {
                    new byte[inputFileStream.Length]
                };
			}

            // Read file into Buffer
            try
            {
                for (int i = 0; inputFileStream.Position < inputFileStream.Length; i++)
				{
                    //With the new addition of 2 dimensional arrays, we're going to await this call to make sure it finishes.
                    await inputFileStream.ReadAsync(originalIsoFile[i]);
                    //And then flush the stream immediately after since the buffer is going to be very full.
                    inputFileStream.Flush();
                }
            }
            finally
            {
                inputFileStream.Dispose();
            }

            bool wasChanged = false;
            long diff = 0;
            rootLba = BitConverter.ToUInt32(originalIsoFile[0][32926..32930]);
            rootLength = BitConverter.ToUInt32(originalIsoFile[0][32934..32938]);

            //Create a queue of search tasks to find all the files in the iso.
            Task[] oldFilePrepQueue = new Task[args.Length / 2];
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

            filesForReplacement = filesForReplacement.OrderByDescending(fileNameLocation => fileNameLocation.Value).ToDictionary(pair => pair.Key, pair => pair.Value);

            newIsoFile = new byte[originalIsoFile.Length][];

            for (int i = 0; i < originalIsoFile.Length; i++)
            {
                newIsoFile[i] = new byte[originalIsoFile[i].Length];
                Array.Copy(originalIsoFile[i], newIsoFile[i], originalIsoFile[i].Length);
            }

            Task[] replaceTasks = new Task[oldFilePrepQueue.Length];

            for (int i = 1; i < args.Length; i += 2)
            {
                long originalSize = newIsoFile.Sum(underlyingArray => (long)underlyingArray.Length);
                //Run the Replace
                Console.WriteLine("- replacing file " + args[i].Replace("\"", ""));
                replaceTasks[i/2] = Replace(args[i].Replace("\"", ""), args[i + 1].Replace("\"", ""));
                diff += newIsoFile.Sum(underlyingArray => (long)underlyingArray.Length) - originalSize;
                if (diff != 0)
                {
                    wasChanged = true;
                }
            }

            Task.WaitAll(replaceTasks);

            //Delete the old file now that we've got all that we need.
            System.IO.File.Delete(args[0]);

            //Write out the new file.
            GetFileWriteStream(args[0], out FileStream outputStream);

            // Write file from Buffer
            try
            {
                //Writing new iso file
                using BinaryWriter binaryWriter = new BinaryWriter(outputStream);
                for(int i = 0; i < newIsoFile.Length; i++)
                {
                    //Loop through all byte arrays as necessary and flush the buffer after each byte array since the size of the array could be massive
                    binaryWriter.Write(newIsoFile[i]);
                    binaryWriter.Flush();                    
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

        static async Task<byte[][]> Replace(string oldName, string newName)
        {
            // get data from the primary volume descriptor
            uint imageSectors = BitConverter.ToUInt32(newIsoFile[0][32848..32852]);
            
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

            //Calculate the LBA's position in the array/s
            
            byte[] fileLBABytes = new byte[4];
            for (long fileLbaArrayPos = sectorSize * foundLBA + foundOffset + 2, i = 0; i < 4; fileLbaArrayPos++, i++)
                fileLBABytes[i] = newIsoFile[fileLbaArrayPos / Array.MaxLength][fileLbaArrayPos % Array.MaxLength]; // We have to loop around the array like this because the bytes we're looking for could end up flowing across two of our internal arrays.
            uint fileLBA = BitConverter.ToUInt32(fileLBABytes); //0x02 = 2

            //size difference in sectors
            int diff = BitConverter.ToInt32(BitConverter.GetBytes(newFileSectorCount - oldFileSectorCount));

            //As a change from the original C code, we won't be creating a duplicate file just for reading as we update since we're storing the whole file in memory.
            uint lba = fileLBA;

            // update the new file

            if (newFileSectorCount != 0)
            {
                if(newFileSectorCount != oldFileSectorCount)
                {
                    ExchangeInequalSectors(lba++, oldFileSectorCount, newFileSectors[newName]);
                }
                else
                {
                    ExchangeEqualSectors(lba++, newFileSectors[newName]);
                }
            }

            if (newFileSize != oldFileSize)
            {
                // update the file size
                System.Console.WriteLine("- updating file size");
                uint littleEndian = newFileSize;
                uint bigEndian = ChangeEndian(littleEndian);
                byte[] littleEndianBytes = BitConverter.GetBytes(littleEndian);
                byte[] bigEndianBytes = BitConverter.GetBytes(bigEndian);
                for (long littleEndianPosition = sectorSize * foundLBA + foundOffset + 10, byteArrayIndex = 0; byteArrayIndex < 4; byteArrayIndex++, littleEndianPosition++)
                {
                    //Replace the old little endian bytes
                    newIsoFile[littleEndianPosition/Array.MaxLength][littleEndianPosition%Array.MaxLength] = littleEndianBytes[byteArrayIndex];
                    //Replace the old big endian bytes
                    newIsoFile[(littleEndianPosition+4) / Array.MaxLength][(littleEndianPosition+4) % Array.MaxLength] = bigEndianBytes[byteArrayIndex];
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
                for (long littleEndianPosition = sectorSize * DESCRIPTOR_LBA + dataOffset + TOTAL_SECTORS, byteArrayIndex = 0; byteArrayIndex < 4; byteArrayIndex++, littleEndianPosition++)
                {
                    //Replace the old little endian bytes
                    newIsoFile[littleEndianPosition / Array.MaxLength][littleEndianPosition % Array.MaxLength] = littleEndianBytes[byteArrayIndex];
                    //Replace the old big endian bytes
                    newIsoFile[(littleEndianPosition + 4) / Array.MaxLength][(littleEndianPosition + 4) % Array.MaxLength] = bigEndianBytes[byteArrayIndex];
                }

                // update the path tables
                System.Console.WriteLine("- updating path tables");
                //Unsure about this next block
                for (int i = 0; i < 4; i++)
                {
                    uint tblLen = BitConverter.ToUInt32(newIsoFile[0][32900..32904]);

                    byte[] uIntByteSwap = new byte[4];

                    //As the comments above and below state, we have to loop around bytes in the array like this. See the above explanation.
                    for (long tblLBAPos = sectorSize * DESCRIPTOR_LBA + dataOffset + TABLE_PATH_LBA + 4 * i, uIntBytePos = 0; uIntBytePos < 4; tblLBAPos++, uIntBytePos++)
                        uIntByteSwap[uIntBytePos] = newIsoFile[tblLBAPos / Array.MaxLength][tblLBAPos % Array.MaxLength];

                    uint tblLBA = BitConverter.ToUInt32(uIntByteSwap);

                    if (tblLBA != 0)
                    {
                        if (i == 2) //0x2 = 2 Bit wise & for byte comparison in C.
                        {
                            tblLBA = ChangeEndian(tblLBA);
                        }
                        PathTable(newIsoFile, tblLBA, tblLen, fileLBA, diff, i == 2); //0x2 = 2
                    }
                }

                // update the file/folder LBAs
                System.Console.WriteLine("- updating entire TOCs");

                TOC(newIsoFile, rootLba, rootLength, foundPosition, fileLBA, diff);

            }
            
            return newIsoFile;
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
            ulong totalSectors = (ulong)((len + sectorSize - 1) / sectorSize);
            for (uint i = 0; i < totalSectors; i++)
            {
                uint nBytes;
                for (long position = 0; position < sectorData && (dataOffset + position + 4) < originalIsoFileLength; position += nBytes)
                {
                    if (sectorSize * (lba + i) + dataOffset + position >= originalIsoFileLength)
                    {
                        break;
                    }
                    //field size
                    long arrayPos = sectorSize * (lba + i) + dataOffset + position;
                    nBytes = originalIsoFile[arrayPos/Array.MaxLength][arrayPos%Array.MaxLength];
                    if (nBytes == 0)
                    {
                        break;
                    }

                    //name size
                    long nCharsPos = sectorSize * (lba + i) + dataOffset + position + 32;
                    byte nChars = originalIsoFile[nCharsPos/Array.MaxLength][nCharsPos%Array.MaxLength]; //0x020 = 32

                    byte[] name = new byte[nChars];
                    //As the comments above and below state, we have to loop around bytes in the array like this. See the above explanation.
                    for (long nameArrayPos = sectorSize * (lba + i) + dataOffset + position + 33, namePos = 0; namePos < name.Length; nameArrayPos++, namePos++)
                        name[namePos] = originalIsoFile[nameArrayPos / Array.MaxLength][(int)(nameArrayPos % Array.MaxLength)];

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
                        long directoryMarkerPos = sectorSize * (lba + i) + dataOffset + position + 25;                        
                        var normalizedName = newPath.Contains('\0') ? newPath.Substring(0,newPath.IndexOf('\0')) : newPath; //Dear Atlus and Sony, why do you do this to me? Why did you hide a string terminating character in your file name with garbage after?

                        if (originalIsoFile[directoryMarkerPos/Array.MaxLength][directoryMarkerPos%Array.MaxLength] == 2) // 0x002 = 2
                        {
                            //Recursive Search Through Folders

                            byte[] uIntByteSwap = new byte[4];

                            //As the comments above and below state, we have to loop around bytes in the array like this. See the above explanation.
                            for (long newLbaPos = sectorSize * (lba + i) + dataOffset + position + 2, uIntBytePos = 0; uIntBytePos < 4; newLbaPos++, uIntBytePos++)
                                uIntByteSwap[uIntBytePos] = originalIsoFile[newLbaPos / Array.MaxLength][(int)(newLbaPos % Array.MaxLength)];

                            // 0x019 = 25, Bitwise & in C compares two bytes for equality.
                            uint newLBA = BitConverter.ToUInt32(uIntByteSwap); // 0x002 = 2

                            //As the comments above and below state, we have to loop around bytes in the array like this. See the above explanation.
                            for (long newLenPos = sectorSize * (lba + i) + dataOffset + position + 10, uIntBytePos = 0; uIntBytePos < 4; newLenPos++, uIntBytePos++)
                                uIntByteSwap[uIntBytePos] = originalIsoFile[newLenPos / Array.MaxLength][(int)(newLenPos % Array.MaxLength)];

                            uint newLen = BitConverter.ToUInt32(uIntByteSwap); // 0x00A = 10

                            ulong found = await Search(fileName, newPath, newLBA, newLen);

                            if (found != 0)
                            {
                                return found;
                            }
                        }
                        
                        // compare names - case insensitive
                        else if (fileName.Equals(normalizedName, StringComparison.InvariantCultureIgnoreCase))
                        {
                            // file found
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

        static void PathTable(byte[][] originalIso, uint lba, uint len, uint lbaOld, int diff, bool sw)
        {
            long isoLength = originalIso.Sum(underlyingArray => (long)underlyingArray.Length);

            uint nBytes;

            for (uint pos = 0; pos < len; pos += 8 + nBytes + (nBytes & 0x1)) //0x01 = 1 Oh and this one is actually used as a bit-wise & so I'm leaving it in hex
            {
                if (sectorSize * lba + dataOffset + pos >= isoLength)
                {
                    break;
                }
                //field size
                long nBytePos = sectorSize * lba + dataOffset + pos;
                nBytes = originalIso[nBytePos/Array.MaxLength][nBytePos%Array.MaxLength];
                if (nBytes == 0)
                {
                    break;
                }
                
                byte[] uIntByteSwap = new byte[4];
                for (long newLBAPos = sectorSize * lba + dataOffset + pos + 2, uIntBytePos = 0; uIntBytePos < 4; newLBAPos++, uIntBytePos++)
                    uIntByteSwap[uIntBytePos] = originalIso[newLBAPos / Array.MaxLength][newLBAPos % Array.MaxLength];

                //position
                uint newLBA = BitConverter.ToUInt32(uIntByteSwap); //0x002 = 2
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
                    for (long newLBAPos = sectorSize * lba + dataOffset + pos + 2, newLBABytesIterator = 0; newLBABytesIterator < 4; newLBAPos++, newLBABytesIterator++)
                        originalIso[newLBAPos/Array.MaxLength][newLBAPos%Array.MaxLength] = newLBABytes[newLBABytesIterator];
                }
            }
        }

        static void TOC(byte[][] originalIso, uint lba, uint len, ulong found, uint lbaOld, int diff)
        {
            long isoFileLength = originalIso.Sum(underlyingByteArray => (long)underlyingByteArray.Length);

            // total sectors
            long totalSectors = (len + sectorSize - 1) / sectorSize;

            for (uint i = 0; i < totalSectors; i++)
            {
                uint nBytes;
                for (uint pos = 0; pos < len; pos += nBytes)
                {
                    if (sectorSize * (lba + i) + dataOffset + pos >= isoFileLength)
                    {
                        break;
                    }
                    long nBytesPos = sectorSize * (lba + i) + dataOffset + pos;
                    //field size
                    nBytes = originalIso[nBytesPos/Array.MaxLength][nBytesPos%Array.MaxLength];
                    if (nBytes == 0)
                    {
                        break;
                    }

                    //name size
                    long nCharsPos = sectorSize * (lba + i) + dataOffset + pos + 32;
                    byte nChars = originalIso[nCharsPos/Array.MaxLength][nCharsPos%Array.MaxLength]; //0x020 = 32
                    byte[] name = new byte[nChars];
                    for (long nameIsoFilePos = sectorSize * (lba + i) + dataOffset + pos + 33, namePos = 0; namePos < name.Length; nameIsoFilePos++, namePos++)
                        name[namePos] = originalIso[nameIsoFilePos / Array.MaxLength][nameIsoFilePos % Array.MaxLength];
                    string nameString = Encoding.ASCII.GetString(name);

                    // position
                    byte[] uIntByteSwap = new byte[4];
                    for (long newLBAPos = sectorSize * (lba + i) + dataOffset + pos + 2, uIntByteIterator = 0; uIntByteIterator < 4; newLBAPos++, uIntByteIterator++)
                        uIntByteSwap[uIntByteIterator] = originalIso[newLBAPos / Array.MaxLength][newLBAPos % Array.MaxLength];

                    uint newLBA = BitConverter.ToUInt32(uIntByteSwap); // 0x002 = 2

                    // needed to change a 0-bytes file with more 0-bytes files (same LBA)
                    ulong newfound = (ulong)(lba + i) * sectorSize + dataOffset + pos;

                    if ((newLBA > lbaOld) || ((newLBA == lbaOld) && (newfound > found)))
                    {
                        //update sector if needed
                        newLBA += (uint)diff;

                        byte[] newLBABytes = BitConverter.GetBytes(newLBA);
                        byte[] bigEndianBytes = BitConverter.GetBytes(ChangeEndian(newLBA));

                        for (long littleEndianPos = sectorSize * (lba + i) + dataOffset + pos + 2, byteIterator = 0; byteIterator < 4; byteIterator++, littleEndianPos++)
                        {
                            originalIso[littleEndianPos/ Array.MaxLength][littleEndianPos % Array.MaxLength] = newLBABytes[byteIterator]; // 0x002 = 2
                            originalIso[(littleEndianPos + 4)/Array.MaxLength][(littleEndianPos+4)%Array.MaxLength] = bigEndianBytes[byteIterator]; // 0x006 = 6
                        }
                    }

                    // check the name except for '.' and '..' entries
                    if ((nChars != 1) || ((name[0] != 0) && (name[0] != 1))) // 0x0 = 0 and 0x1 = repeat previous character in ASCII
                    {
                        long folderPosition = sectorSize * (lba + i) + dataOffset + pos + 25;
                        //recursive update in folders
                        if (originalIso[folderPosition/Array.MaxLength][folderPosition%Array.MaxLength] == 2) //0x02 = 2
                        {
                            for (long newLenPos = sectorSize * (lba + i) + dataOffset + pos + 10, uIntByteIterator = 0; uIntByteIterator < 4; newLenPos++, uIntByteIterator++)
                                uIntByteSwap[uIntByteIterator] = originalIsoFile[newLenPos / Array.MaxLength][newLenPos % Array.MaxLength];

                            uint newLen = BitConverter.ToUInt32(uIntByteSwap); // 0x00A = 10

                            TOC(originalIso, newLBA, newLen, found, lbaOld, diff);
                        }
                    }
                }
            }
        }

        static uint ChangeEndian(uint value)
        {
            return BitConverter.ToUInt32(BitConverter.GetBytes(value).Reverse().ToArray());
        }

        static void ExchangeEqualSectors(uint offset, in byte[] dataToExchange)
        {
            while (fileLock)
            {
                Thread.Sleep(rngGenerator.Next(0, 10));
                //Wait for file to be unlocked
            }
            fileLock = true;

            //Fortunately, because our files are of equal sectors, we can just copy them directly over to the appopriate position without worrying.
            //This is one of many major optimizations over the original UMDReplace
            if((offset * sectorSize % Array.MaxLength) + dataToExchange.Length > Array.MaxLength)
			{
                //I'm unsure if this code will ever be hit, but I am including it in the off chance that it does manage to get hit. I personally don't have any imaginable circumstances that could create this, but hey, why not be safe?
                long splitPoint = ((offset * sectorSize % Array.MaxLength) + dataToExchange.Length) % Array.MaxLength;
                long startingArray = (offset * sectorSize) / Array.MaxLength;
                long startingPoint = (offset * sectorSize) % Array.MaxLength;
                Array.Copy(dataToExchange, 0, newIsoFile[startingArray], startingPoint, splitPoint-1);
                Array.Copy(dataToExchange, splitPoint, newIsoFile[startingArray+1], 0, dataToExchange.Length - splitPoint);
            }
            else
                Array.Copy(dataToExchange, 0, newIsoFile[(offset*sectorSize)/Array.MaxLength], (offset * sectorSize)%Array.MaxLength, dataToExchange.Length);

            fileLock = false;
        }

        static void ExchangeInequalSectors(uint offset, uint originalFileSectors, in byte[] dataToExchange)
        {
            while (fileLock)
            {
                Thread.Sleep(rngGenerator.Next(0, 10));
                //Wait for file to be unlocked
            }
            fileLock = true;

            long originalFileSize = sectorSize*originalFileSectors;
            long newSize = newIsoFile[0..^1].LongLength * Array.MaxLength + (newIsoFile[^1].LongLength + dataToExchange.Length - originalFileSize);
            long startingArray = offset * sectorSize / Array.MaxLength;
            byte[][] exchangedIso = new byte[newSize/Array.MaxLength+1][];
            
            for (int i = 0; i < startingArray; i++)
                exchangedIso[i] = newIsoFile[i];
            for (long arrayIterator = startingArray; arrayIterator < exchangedIso.Length - 1; arrayIterator++)
                exchangedIso[arrayIterator] = new byte[Array.MaxLength];
            exchangedIso[^1] = new byte[newSize % Array.MaxLength];

            long startPosition = (offset * sectorSize)%Array.MaxLength;

            Array.Copy(newIsoFile[startingArray], 0, exchangedIso[startingArray], 0, startPosition); //Copy over the first half of the array into the new array
            Array.Copy(dataToExchange, 0, exchangedIso[startingArray], startPosition, dataToExchange.Length); //Copy over the new bytes

            long dataCopied = (startingArray) * (startPosition * Array.MaxLength) + startPosition + dataToExchange.LongLength;

            if(dataToExchange.Length > originalFileSize)
			{
                //If the new file is bigger than the old one.

                //First, copy the rest of the old array as much as our new array will allow
                Array.Copy(newIsoFile[startingArray], startPosition + originalFileSize, exchangedIso[startingArray], startPosition + dataToExchange.Length, exchangedIso[startingArray].Length - (startPosition + dataToExchange.Length)); // Copy the old array contents starting from the end of the replaced file.
                dataCopied += (exchangedIso[startingArray].Length - (startPosition + dataToExchange.Length));

                if (startingArray != exchangedIso.Length-1)
				{
                    //If there is still more to copy from the old arrays
                    long splitPosition = startPosition + originalFileSize + exchangedIso[startingArray].Length - (startPosition + dataToExchange.Length);

                    //Copy up until the point we're on the last old array.
                    for (long oldArrayIterator = startingArray + 1; oldArrayIterator < exchangedIso.Length; oldArrayIterator++)
                    {
                        //Finish copying over the contents of the old previous array into our new next array
                        Array.Copy(newIsoFile[oldArrayIterator - 1], splitPosition, exchangedIso[oldArrayIterator], 0, newIsoFile[oldArrayIterator - 1].Length - splitPosition);
                        dataCopied += (exchangedIso[startingArray].Length - (startPosition + dataToExchange.Length));

                        //Now fill up our new next array as much as we can with its corresponding old array partner
                        Array.Copy(newIsoFile[oldArrayIterator], 0, exchangedIso[oldArrayIterator], newIsoFile[oldArrayIterator - 1].Length - splitPosition, exchangedIso[oldArrayIterator].Length - (newIsoFile[oldArrayIterator - 1].Length - splitPosition));
                        dataCopied += (exchangedIso[oldArrayIterator].Length - (newIsoFile[oldArrayIterator - 1].Length - splitPosition));
                    }

                    if(dataCopied < newSize)
                        Array.Copy(newIsoFile[^1], splitPosition, exchangedIso[^1], 0, exchangedIso[^1].Length);
                }
            }
            else
			{
                //If the old file was bigger than the new one

                //First, copy the rest of the old array as applicable
                Array.Copy(newIsoFile[startingArray], startPosition + originalFileSize, exchangedIso[startingArray], startPosition + dataToExchange.Length, newIsoFile[startingArray].Length - (startPosition + originalFileSize)); // Copy the old array contents starting from the end of the replaced file.
                dataCopied += (newIsoFile[startingArray].Length - (startPosition + originalFileSize));

                if (startingArray != newIsoFile.Length-1)
                {
                    //If there is still more to copy from the old arrays
                    long splitPosition = startPosition + dataToExchange.Length + newIsoFile[startingArray].Length - (startPosition + originalFileSize);

                    //Copy up until the point we're on the last old array.
                    for (long oldArrayIterator = startingArray + 1; oldArrayIterator < newIsoFile.Length; oldArrayIterator++)
                    {
                        //Fill up the missing space with the bytes from the next array.
                        Array.Copy(newIsoFile[oldArrayIterator], 0, exchangedIso[oldArrayIterator - 1], splitPosition, exchangedIso[oldArrayIterator - 1].Length - splitPosition);
                        dataCopied += (exchangedIso[oldArrayIterator - 1].Length - splitPosition);

                        //Now that our array iterators are lined up, dump whatever is left from the array that we just used to fill up the bytes
                        Array.Copy(newIsoFile[oldArrayIterator], exchangedIso[oldArrayIterator - 1].Length - splitPosition, exchangedIso[oldArrayIterator], 0, newIsoFile[oldArrayIterator].Length - (exchangedIso[oldArrayIterator - 1].Length - splitPosition));
                        dataCopied += (newIsoFile[oldArrayIterator].Length - (exchangedIso[oldArrayIterator - 1].Length - splitPosition));
                    }

                    //Grab the last contents of the old array and drop them into place for the new final array.
                    if(dataCopied < newSize)
                        Array.Copy(newIsoFile[^1], 0, exchangedIso[^1], splitPosition, exchangedIso[^1].Length - splitPosition);
                }
            }

            newIsoFile = exchangedIso;
            fileLock = false;
        }

        static async Task<byte[]> ConvertToSectors(byte[] fileData)
        {
            if (fileData.Length % sectorSize == 0)
                return fileData.ToArray();
            byte[] bytePadding = new byte[sectorSize - fileData.Length % sectorSize];
            Array.Fill<byte>(bytePadding, 0);
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

            byte[] oldFileSizeBytes = new byte[4];
            //Again, unfortunately, we have to loop around the array. See my above comment as to why.
            for (long oldFileSizePosStart = sectorSize * foundLBA + foundOffset + 10, i = 0; i < 4; oldFileSizePosStart++, i++)
                oldFileSizeBytes[i] = originalIsoFile[oldFileSizePosStart / Array.MaxLength][(int)(oldFileSizePosStart % Array.MaxLength)];

            uint oldFileSize = BitConverter.ToUInt32(oldFileSizeBytes);
            oldFileSizes.Add(normalizedOldFileName, oldFileSize);
            oldFileSectorCounts.Add(normalizedOldFileName, (oldFileSize + sectorData - 1) / sectorData);
        }
    }
}