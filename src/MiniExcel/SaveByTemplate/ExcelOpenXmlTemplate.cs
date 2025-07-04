﻿using MiniExcelLibs.Utils;
using MiniExcelLibs.Zip;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace MiniExcelLibs.OpenXml.SaveByTemplate
{
    internal partial class ExcelOpenXmlTemplate : IExcelTemplateAsync
    {
#if NET7_0_OR_GREATER
        [GeneratedRegex("(?<={{).*?(?=}})")] private static partial Regex ExpressionRegex();
        private static readonly Regex _isExpressionRegex = ExpressionRegex();
#else
        private static readonly Regex _isExpressionRegex = new Regex("(?<={{).*?(?=}})");
#endif 
        private static readonly XmlNamespaceManager _ns;

        private readonly Stream _outputFileStream;
        private readonly OpenXmlConfiguration _configuration;
        private readonly IInputValueExtractor _inputValueExtractor;
        private readonly StringBuilder _calcChainContent = new StringBuilder();

        static ExcelOpenXmlTemplate()
        {
            _ns = new XmlNamespaceManager(new NameTable());
            _ns.AddNamespace("x", Config.SpreadsheetmlXmlns);
            _ns.AddNamespace("x14ac", Config.SpreadsheetmlXml_x14ac);
        }

        public ExcelOpenXmlTemplate(Stream stream, IConfiguration configuration, InputValueExtractor inputValueExtractor)
        {
            _outputFileStream = stream;
            _configuration = (OpenXmlConfiguration)configuration ?? OpenXmlConfiguration.DefaultConfig;
            _inputValueExtractor = inputValueExtractor;
        }

        [Zomp.SyncMethodGenerator.CreateSyncVersion]
        public async Task SaveAsByTemplateAsync(string templatePath, object value, CancellationToken cancellationToken = default)
        {
            using (var stream = FileHelper.OpenSharedRead(templatePath))
                await SaveAsByTemplateImplAsync(stream, value, cancellationToken).ConfigureAwait(false);
        }

        [Zomp.SyncMethodGenerator.CreateSyncVersion]
        public async Task SaveAsByTemplateAsync(byte[] templateBytes, object value, CancellationToken cancellationToken = default)
        {
            using (Stream stream = new MemoryStream(templateBytes))
                await SaveAsByTemplateImplAsync(stream, value, cancellationToken).ConfigureAwait(false);
        }

        [Zomp.SyncMethodGenerator.CreateSyncVersion]
        internal async Task SaveAsByTemplateImplAsync(Stream templateStream, object value, CancellationToken cancellationToken = default)
        {
            //only support xlsx
            //templateStream.CopyTo(_outputFileStream);

            // foreach all templateStream and create file for _outputFileStream and not create sheet file
            templateStream.Position = 0;
            using var templateReader = await ExcelOpenXmlSheetReader.CreateAsync(templateStream, null, cancellationToken: cancellationToken).ConfigureAwait(false);
            using var outputFileArchive = new ExcelOpenXmlZip(_outputFileStream, mode: ZipArchiveMode.Create, true, Encoding.UTF8, isUpdateMode: false);
            try
            {
                outputFileArchive.entries = templateReader._archive.zipFile.Entries; //TODO:need to remove
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"An invalid valid OpenXml zip archive was detected, please check or open an issue for this error: {e.Message}");
            }

            foreach (var entry in templateReader._archive.zipFile.Entries)
            {
                outputFileArchive._entries.Add(entry.FullName.Replace('\\', '/'), entry);
            }
            
            templateStream.Position = 0;
            using (var originalArchive = new ZipArchive(templateStream, ZipArchiveMode.Read))
            {
                // Create a new zip file for writing
                //using (FileStream newZipStream = new FileStream(newZipPath, FileMode.Create))
                //using (ZipArchive newArchive = new ZipArchive(_outputFileStream, ZipArchiveMode.Create))
                
                // Iterate through each entry in the original archive
                foreach (ZipArchiveEntry entry in originalArchive.Entries)
                {
                    if (entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.StartsWith("/xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.Contains("xl/calcChain.xml")
                    )
                        continue;
                    
                    // Create a new entry in the new archive with the same name
                    var newEntry = outputFileArchive.zipFile.CreateEntry(entry.FullName);

                    // Copy the content of the original entry to the new entry
                    using (Stream originalEntryStream = entry.Open())
                    using (Stream newEntryStream = newEntry.Open())
                    {
                        await originalEntryStream.CopyToAsync(newEntryStream
#if NETCOREAPP2_1_OR_GREATER
                                    , cancellationToken
#endif
                            ).ConfigureAwait(false);
                    }
                }

                //read sharedString
                var templateSharedStrings = templateReader._sharedStrings;
                templateStream.Position = 0;

                //read all xlsx sheets
                var templateSheets = templateReader._archive.zipFile.Entries
                    .Where(w =>
                        w.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) ||
                        w.FullName.StartsWith("/xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase));

                int sheetIdx = 0;
                foreach (var templateSheet in templateSheets)
                {
                    //every time need to use new XRowInfos or it'll cause duplicate problem: https://user-images.githubusercontent.com/12729184/115003101-0fcab700-9ed8-11eb-9151-ca4d7b86d59e.png
                    _xRowInfos = new List<XRowInfo>();
                    _xMergeCellInfos = new Dictionary<string, XMergeCell>();
                    _newXMergeCellInfos = new List<XMergeCell>();

                    var templateSheetStream = templateSheet.Open();
                    var templateFullName = templateSheet.FullName;

                    var inputValues = _inputValueExtractor.ToValueDictionary(value);
                    var outputZipEntry = outputFileArchive.zipFile.CreateEntry(templateFullName);
                    using (var outputZipSheetEntryStream = outputZipEntry.Open())
                    {
                        GenerateSheetXmlImplByCreateMode(templateSheet, outputZipSheetEntryStream, templateSheetStream, inputValues, templateSharedStrings, false);
                        //doc.Save(zipStream); //don't do it because: https://user-images.githubusercontent.com/12729184/114361127-61a5d100-9ba8-11eb-9bb9-34f076ee28a2.png
                        // disposing writer disposes streams as well. read and parse calc functions before that
                        sheetIdx++;
                        _calcChainContent.Append(CalcChainHelper.GetCalcChainContent(_calcChainCellRefs, sheetIdx));
                    }
                }

                // create mode we need to not create first then create here
                var calcChain = outputFileArchive.entries.FirstOrDefault(e => e.FullName.Contains("xl/calcChain.xml"));
                if (calcChain != null)
                {
                    var calcChainPathName = calcChain.FullName;
                    //calcChain.Delete();

                    var calcChainEntry = outputFileArchive.zipFile.CreateEntry(calcChainPathName);
                    using (var calcChainStream = calcChainEntry.Open())
                    {
                        await CalcChainHelper.GenerateCalcChainSheetAsync(calcChainStream, _calcChainContent.ToString(), cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    foreach (ZipArchiveEntry entry in originalArchive.Entries)
                    {
                        if (entry.FullName.Contains("xl/calcChain.xml"))
                        {
                            var newEntry = outputFileArchive.zipFile.CreateEntry(entry.FullName);

                            // Copy the content of the original entry to the new entry
                            using (Stream originalEntryStream = entry.Open())
                            using (Stream newEntryStream = newEntry.Open())
                            {
                                await originalEntryStream.CopyToAsync(newEntryStream
#if NETCOREAPP2_1_OR_GREATER
                                    , cancellationToken
#endif
                                    ).ConfigureAwait(false);
                            }
                        }
                    }
                }

                outputFileArchive.zipFile.Dispose();
            }
        }
    }
}