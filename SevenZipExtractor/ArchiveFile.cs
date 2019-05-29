﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SevenZipExtractor
{
    public class ArchiveFile : IDisposable
    {
        private static readonly SevenZipHandle SevenZipHandle;

        private readonly IInArchive archive;
        private readonly InStreamWrapper archiveStream;
        private IList<Entry> entries;

        //private string libraryFilePath;

        static ArchiveFile()
        {
            SevenZipHandle = InitializeAndValidateLibrary();
        }

        public ArchiveFile(string archiveFilePath)
        {
            if (!File.Exists(archiveFilePath))
            {
                throw new SevenZipException("Archive file not found");
            }

            var format = GetFormat(archiveFilePath);

            this.archive = SevenZipHandle.CreateInArchive(Formats.FormatGuidMapping[format]);
            this.archiveStream = new InStreamWrapper(File.OpenRead(archiveFilePath));
        }

        /// <summary>
        /// It is recommended to use 'GuessFormatFromExtension' func by the file extension to identify the file format. 
        /// </summary>
        /// <param name="archiveStream"></param>
        /// <param name="format">If the format is null, will read the stream header to identify the format. but it's not always right.</param>
        public ArchiveFile(Stream archiveStream, SevenZipFormat? format = null)
        {
            if (archiveStream == null)
            {
                throw new SevenZipException("archiveStream is null");
            }

            if (format == null)
            {
                SevenZipFormat guessedFormat;

                if (GuessFormatFromSignature(archiveStream, out guessedFormat))
                {
                    format = guessedFormat;
                }
                else
                {
                    throw new SevenZipException("Unable to guess format automatically");
                }
            }

            this.archive = SevenZipHandle.CreateInArchive(Formats.FormatGuidMapping[format.Value]);
            this.archiveStream = new InStreamWrapper(archiveStream);
        }

        public void Extract(string outputFolder, bool overwrite = false) 
        {
            this.Extract(entry => 
            {
                string fileName = Path.Combine(outputFolder, entry.FileName);

                if (entry.IsFolder)
                {
                    return fileName;
                }

                if (!File.Exists(fileName) || overwrite) 
                {
                    return fileName;
                }

                return null;
            });
        }

        public void Extract(Func<Entry, string> getOutputPath) 
        {
            IList<Stream> fileStreams = new List<Stream>();

            try 
            {
                foreach (Entry entry in Entries)
                {
                    string outputPath = getOutputPath(entry);

                    if (outputPath == null) // getOutputPath = null means SKIP
                    {
                        fileStreams.Add(null);
                        continue;
                    }

                    if (entry.IsFolder)
                    {
                        Directory.CreateDirectory(outputPath);
                        fileStreams.Add(null);
                        continue;
                    }

                    string directoryName = Path.GetDirectoryName(outputPath);

                    if (!string.IsNullOrWhiteSpace(directoryName))
                    {
                        Directory.CreateDirectory(directoryName);
                    }

                    fileStreams.Add(File.Create(outputPath));
                }

                this.archive.Extract(null, 0xFFFFFFFF, 0, new ArchiveStreamsCallback(fileStreams));
            }
            finally
            {
                foreach (Stream stream in fileStreams) 
                {
                    if (stream != null)
                    {
                        stream.Dispose();
                    }
                }
            }
        }

        public IList<Entry> Entries
        {
            get
            {
                if (this.entries != null)
                {
                    return this.entries;
                }

                ulong checkPos = 32 * 1024;
                int open = this.archive.Open(this.archiveStream, ref checkPos, null);

                if (open != 0)
                {
                    throw new SevenZipException("Unable to open archive");
                }

                uint itemsCount = this.archive.GetNumberOfItems();

                this.entries = new List<Entry>();

                for (uint fileIndex = 0; fileIndex < itemsCount; fileIndex++)
                {
                    string fileName = this.GetProperty<string>(fileIndex, ItemPropId.kpidPath);
                    bool isFolder = this.GetProperty<bool>(fileIndex, ItemPropId.kpidIsFolder);
                    bool isEncrypted = this.GetProperty<bool>(fileIndex, ItemPropId.kpidEncrypted);
                    ulong size = this.GetProperty<ulong>(fileIndex, ItemPropId.kpidSize);
                    ulong packedSize = this.GetProperty<ulong>(fileIndex, ItemPropId.kpidPackedSize);
                    DateTime creationTime = this.GetProperty<DateTime>(fileIndex, ItemPropId.kpidCreationTime);
                    DateTime lastWriteTime = this.GetProperty<DateTime>(fileIndex, ItemPropId.kpidLastWriteTime);
                    DateTime lastAccessTime = this.GetProperty<DateTime>(fileIndex, ItemPropId.kpidLastAccessTime); 
                    UInt32 crc = this.GetProperty<UInt32>(fileIndex, ItemPropId.kpidCRC);
                    UInt32 attributes = this.GetProperty<UInt32>(fileIndex, ItemPropId.kpidAttributes);
                    string comment = this.GetProperty<string>(fileIndex, ItemPropId.kpidComment);
                    string hostOS = this.GetProperty<string>(fileIndex, ItemPropId.kpidHostOS);
                    string method = this.GetProperty<string>(fileIndex, ItemPropId.kpidMethod);

                    bool isSplitBefore = this.GetProperty<bool>(fileIndex, ItemPropId.kpidSplitBefore);
                    bool isSplitAfter = this.GetProperty<bool>(fileIndex, ItemPropId.kpidSplitAfter);

                    this.entries.Add(new Entry(this.archive, fileIndex)
                    {
                        FileName = fileName,
                        IsFolder = isFolder,
                        IsEncrypted = isEncrypted,
                        Size = size,
                        PackedSize = packedSize,
                        CreationTime = creationTime,
                        LastWriteTime = lastWriteTime,
                        LastAccessTime = lastAccessTime,
                        CRC = crc,
                        Attributes = attributes,
                        Comment = comment,
                        HostOS = hostOS,
                        Method = method,
                        IsSplitBefore = isSplitBefore,
                        IsSplitAfter = isSplitAfter
                    });
                }

                return this.entries;
            }
        }

        private T GetProperty<T>(uint fileIndex, ItemPropId name)
        {
            PropVariant propVariant = new PropVariant();
            this.archive.GetProperty(fileIndex, name, ref propVariant);

            T result = propVariant.VarType != VarEnum.VT_EMPTY
                ? (T) propVariant.GetObject()
                : default(T);

            propVariant.Clear();

            return result;
        }

        private static SevenZipHandle InitializeAndValidateLibrary()
        {
            var libraryFilePath = Get7ZLibraryFilePath();
            try
            {
                var sevenZipHandle = new SevenZipHandle(libraryFilePath);
                return sevenZipHandle;
            }
            catch (Exception e)
            {
                throw new SevenZipException($"Unable to initialize SevenZipHandle, library file path:{libraryFilePath}", e);
            }
        }

        private static string Get7ZLibraryFilePath()
        {
            string libraryFilePath = null;
            //string currentArchitecture = IntPtr.Size == 4 ? "x86" : "x64"; // magic check
            string currentArchitecture = Environment.Is64BitProcess ? "x64" : "x86"; // magic check

            if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "7zDll", currentArchitecture, "7z.dll")))
            {
                libraryFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "7zDll",
                    currentArchitecture, "7z.dll");
            }
            else if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "7z-" + currentArchitecture + ".dll")))
            {
                libraryFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "7z-" + currentArchitecture + ".dll");
            }
            else if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin",
                "7z-" + currentArchitecture + ".dll")))
            {
                libraryFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin",
                    "7z-" + currentArchitecture + ".dll");
            }
            else if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, currentArchitecture, "7z.dll")))
            {
                libraryFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, currentArchitecture, "7z.dll");
            }
            else if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip",
                "7z.dll")))
            {
                libraryFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip",
                    "7z.dll");
            }

            if (string.IsNullOrWhiteSpace(libraryFilePath))
            {
                throw new SevenZipException("libraryFilePath not set");
            }

            if (!File.Exists(libraryFilePath))
            {
                throw new SevenZipException($"7z.dll not found,library file path: {libraryFilePath}");
            }

            return libraryFilePath;
        }

        public static bool GuessFormatFromExtension(string fileExtension, out SevenZipFormat format)
        {
            if (string.IsNullOrWhiteSpace(fileExtension))
            {
                format = SevenZipFormat.Undefined;
                return false;
            }

            fileExtension = fileExtension.TrimStart('.').Trim().ToLowerInvariant();

            if (fileExtension.Equals("rar"))
            {
                // 7z has different GUID for Pre-RAR5 and RAR5, but they have both same extension (.rar)
                // If it is [0x52 0x61 0x72 0x21 0x1A 0x07 0x01 0x00] then file is RAR5 otherwise RAR.
                // https://www.rarlab.com/technote.htm

                // We are unable to guess right format just by looking at extension and have to check signature

                format = SevenZipFormat.Undefined;
                return false;
            }

            if (!Formats.ExtensionFormatMapping.ContainsKey(fileExtension))
            {
                format = SevenZipFormat.Undefined;
                return false;
            }

            format = Formats.ExtensionFormatMapping[fileExtension];
            return true;
        }

        private static SevenZipFormat GetFormat(string archiveFilePath)
        {
            string extension = Path.GetExtension(archiveFilePath);

            if (GuessFormatFromExtension(extension, out var format))
            {
                // great
            }
            else if (GuessFormatFromSignature(archiveFilePath, out format))
            {
                // success
            }
            else
            {
                throw new SevenZipException(Path.GetFileName(archiveFilePath) + " is not a known archive type");
            }

            return format;
        }

        private static bool GuessFormatFromSignature(string filePath, out SevenZipFormat format)
        {
            using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return GuessFormatFromSignature(fileStream, out format);
            }
        }

        private static bool GuessFormatFromSignature(Stream stream, out SevenZipFormat format)
        {
            int longestSignature = Formats.FileSignatures.Values.OrderByDescending(v => v.Length).First().Length;

            byte[] archiveFileSignature = new byte[longestSignature];
            int bytesRead = stream.Read(archiveFileSignature, 0, longestSignature);

            stream.Position -= bytesRead; // go back o beginning

            if (bytesRead != longestSignature)
            {
                format = SevenZipFormat.Undefined;
                return false;
            }

            foreach (KeyValuePair<SevenZipFormat, byte[]> pair in Formats.FileSignatures)
            {
                if (archiveFileSignature.Take(pair.Value.Length).SequenceEqual(pair.Value))
                {
                    format = pair.Key;
                    return true;
                }
            }

            format = SevenZipFormat.Undefined;
            return false;
        }

        ~ArchiveFile()
        {
            this.Dispose(false);
        }

        protected void Dispose(bool disposing)
        {
            if (this.archiveStream != null)
            {
                this.archiveStream.Dispose();
            }

            if (this.archive != null)
            {
                Marshal.ReleaseComObject(this.archive);
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
