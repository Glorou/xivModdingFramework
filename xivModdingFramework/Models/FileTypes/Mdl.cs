﻿// xivModdingFramework
// Copyright © 2018 Rafael Gonzalez - All Rights Reserved
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using SharpDX;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HelixToolkit.SharpDX.Core;
using xivModdingFramework.General.Enums;
using xivModdingFramework.Helpers;
using xivModdingFramework.Items;
using xivModdingFramework.Items.DataContainers;
using xivModdingFramework.Items.Enums;
using xivModdingFramework.Items.Interfaces;
using xivModdingFramework.Models.DataContainers;
using xivModdingFramework.Models.Enums;
using xivModdingFramework.Mods;
using xivModdingFramework.Mods.Enums;
using xivModdingFramework.Resources;
using xivModdingFramework.SqPack.FileTypes;
using BoundingBox = xivModdingFramework.Models.DataContainers.BoundingBox;
using System.Diagnostics;
using xivModdingFramework.Items.Categories;
using HelixToolkit.SharpDX.Core.Core;
using System.Transactions;
using System.Runtime.CompilerServices;
using System.Threading;
using SharpDX.Win32;
using xivModdingFramework.Models.Helpers;
using Newtonsoft.Json;
using xivModdingFramework.Materials.FileTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using xivModdingFramework.Textures.FileTypes;
using xivModdingFramework.Models.ModelTextures;
using SixLabors.ImageSharp.Formats.Bmp;
using xivModdingFramework.Variants.FileTypes;
using SixLabors.ImageSharp.Formats.Png;
using System.Data;
using System.Text.RegularExpressions;
using xivModdingFramework.Materials.DataContainers;
using xivModdingFramework.Cache;
using xivModdingFramework.SqPack.DataContainers;
using xivModdingFramework.Mods.DataContainers;
using xivModdingFramework.Mods.FileTypes;

using Index = xivModdingFramework.SqPack.FileTypes.Index;
using System.Data.SQLite;
using static xivModdingFramework.Cache.XivCache;
using System.Runtime.InteropServices.ComTypes;
using SixLabors.ImageSharp.Metadata.Profiles.Iptc;

namespace xivModdingFramework.Models.FileTypes
{
    public class Mdl
    {
        private const string MdlExtension = ".mdl";
        private readonly DirectoryInfo _gameDirectory;
        private readonly DirectoryInfo _modListDirectory;
        private readonly XivDataFile _dataFile;

        // Simple internal use hashable pair of Halfs.
        private struct HalfUV
        {
            public HalfUV(Half _x, Half _y)
            {
                x = _x;
                y = _y;
            }
            public HalfUV(float _x, float _y)
            {
                x = _x;
                y = _y;
            }

            public Half x;
            public Half y;

            public override int GetHashCode()
            {
                var bx = BitConverter.GetBytes(x);
                var by = BitConverter.GetBytes(y);

                var bytes = new byte[4];
                bytes[0] = bx[0];
                bytes[1] = bx[1];
                bytes[2] = by[0];
                bytes[3] = by[1];

                return BitConverter.ToInt32(bytes, 0);
            }
        }

        private static Dictionary<string, HashSet<HalfUV>> BodyHashes;

        // Retrieve hash list of UVs for use in heuristics.
        private HashSet<HalfUV> GetUVHashSet(string key)
        {
            if (BodyHashes == null)
            {
                BodyHashes = new Dictionary<string, HashSet<HalfUV>>();
            }

            if (BodyHashes.ContainsKey(key))
            {
                return BodyHashes[key];
            }

            try
            {

                var connectString = "Data Source=resources/db/uv_heuristics.db;";
                using (var db = new SQLiteConnection(connectString))
                {
                    db.Open();

                    // Time to go root hunting.
                    var query = "select * from " + key + ";";

                    using (var cmd = new SQLiteCommand(query, db))
                    {
                        var uvs = new HashSet<HalfUV>();
                        using (var reader = new CacheReader(cmd.ExecuteReader()))
                        {
                            while (reader.NextRow())
                            {
                                var uv = new HalfUV();
                                uv.x = reader.GetFloat("x");
                                uv.y = reader.GetFloat("y");
                                uvs.Add(uv);
                            }
                        }
                        BodyHashes[key] = uvs;
                        return BodyHashes[key];
                    }
                }
            }
            catch (Exception ex)
            {
                // blep
            }

            return null;
        }

        public Mdl(DirectoryInfo gameDirectory, XivDataFile dataFile)
        {
            _gameDirectory = gameDirectory;
            _modListDirectory = new DirectoryInfo(Path.Combine(gameDirectory.Parent.Parent.FullName, XivStrings.ModlistFilePath));

            _dataFile = dataFile;
        }

        private byte[] _rawData;

        /// <summary>
        /// Retrieves and clears the RawData value.
        /// </summary>
        /// <returns></returns>
        public byte[] GetRawData()
        {
            var ret = _rawData;
            _rawData = null;
            return ret;
        }


        /// <summary>
        /// Converts and exports an item's MDL file, passing it to the appropriate exporter as necessary
        /// to match the target file extention.
        /// </summary>
        /// <param name="item"></param>
        /// <param name="race"></param>
        /// <param name="submeshId"></param>
        /// <returns></returns>
        public async Task ExportMdlToFile(IItemModel item, XivRace race, string outputFilePath, string submeshId = null, bool includeTextures = true, bool getOriginal = false)
        {
            var mdlPath = await GetMdlPath(item, race, submeshId);
            var mtrlVariant = 1;
            try
            {
                var _imc = new Imc(_gameDirectory);
                mtrlVariant = (await _imc.GetImcInfo(item)).MaterialSet;
            }
            catch (Exception ex)
            {
                // No-op, defaulted to 1.
            }

            await ExportMdlToFile(mdlPath, outputFilePath, mtrlVariant, includeTextures, getOriginal);
        }


        /// <summary>
        /// Converts and exports an item's MDL file, passing it to the appropriate exporter as necessary
        /// to match the target file extention.
        /// </summary>
        /// <param name="mdlPath"></param>
        /// <param name="outputFilePath"></param>
        /// <param name="getOriginal"></param>
        /// <returns></returns>
        public async Task ExportMdlToFile(string mdlPath, string outputFilePath, int mtrlVariant = 1, bool includeTextures = true, bool getOriginal = false)
        {
            // Importers and exporters currently use the same criteria.
            // Any available exporter is assumed to be able to import and vice versa.
            // This may change at a later date.
            var exporters = GetAvailableExporters();
            var fileFormat = Path.GetExtension(outputFilePath).Substring(1);
            fileFormat = fileFormat.ToLower();
            if (!exporters.Contains(fileFormat))
            {
                throw new NotSupportedException(fileFormat.ToUpper() + " File type not supported.");
            }

            var dir = Path.GetDirectoryName(outputFilePath);
            if (!Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            var imc = new Imc(_gameDirectory);
            var model = await GetModel(mdlPath);
            await ExportModel(model, outputFilePath, mtrlVariant, includeTextures);
        }


        /// <summary>
        /// Exports a TTModel file to the given output path.
        /// </summary>
        /// <param name="model"></param>
        /// <param name="outputFilePath"></param>
        /// <returns></returns>
        public async Task ExportModel(TTModel model, string outputFilePath, int mtrlVariant = 1, bool includeTextures = true)
        {
            var exporters = GetAvailableExporters();
            var fileFormat = Path.GetExtension(outputFilePath).Substring(1);
            fileFormat = fileFormat.ToLower();
            if (!exporters.Contains(fileFormat))
            {
                throw new NotSupportedException(fileFormat.ToUpper() + " File type not supported.");
            }



            var dir = Path.GetDirectoryName(outputFilePath);
            if (!Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            // Remove the existing file if it exists, so that the user doesn't get confused thinking an old file is the new one.
            File.Delete(outputFilePath);

            outputFilePath = outputFilePath.Replace("/", "\\");

            // OBJ is a bit of a special, speedy case.  The format both has no textures, and no weights,
            // So we don't need to do any heavy lifting for that stuff.
            if (fileFormat == "obj")
            {
                var obj = new Obj(_gameDirectory);
                obj.ExportObj(model, outputFilePath);
                return;
            }

            if (!model.IsInternal)
            {
                // This isn't *really* true, but there's no case where we are re-exporting TTModel objects
                // right now without them at least having an internal XIV path associated, so I don't see a need to fuss over this,
                // since it would be complicated.
                throw new NotSupportedException("Cannot export non-internal model - Skel data unidentifiable.");
            }

            // The export process could really be sped up by forking threads to do
            // both the bone and material exports at the same time.

            // Pop the textures out so the exporters can reference them.
            if (includeTextures)
            {
                // Fix up our skin references in the model before exporting, to ensure
                // we supply the right material names to the exporters down-chain.
                if (model.IsInternal)
                {
                    ModelModifiers.FixUpSkinReferences(model, model.Source, null);
                }
                await ExportMaterialsForModel(model, outputFilePath, _gameDirectory, mtrlVariant);
            }



            // Save the DB file.

            var cwd = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            var converterFolder = cwd + "\\converters\\" + fileFormat;
            Directory.CreateDirectory(converterFolder);
            var dbPath = converterFolder + "\\input.db";
            model.SaveToFile(dbPath, outputFilePath);


            if (fileFormat == "db")
            {
                // Just want the intermediate file? Just see if we need to move it.
                if (!Path.Equals(outputFilePath, dbPath))
                {
                    File.Delete(outputFilePath);
                    File.Move(dbPath, outputFilePath);
                }
            }
            else
            {
                // We actually have an external importer to use.

                // We don't really care that much about showing the user a log
                // during exports, so we can just do this the simple way.

                var outputFile = converterFolder + "\\result." + fileFormat;

                // Get rid of any existing intermediate output file, in case it causes problems for any converters.
                File.Delete(outputFile);

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = converterFolder + "\\converter.exe",
                        Arguments = "\"" + dbPath + "\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        WorkingDirectory = "" + converterFolder + "",
                        CreateNoWindow = true
                    }
                };

                proc.Start();
                proc.WaitForExit();
                var code = proc.ExitCode;

                if (code != 0)
                {
                    throw new Exception("Exporter threw error code: " + proc.ExitCode);
                }

                // Just move the result file if we need to.
                if (!Path.Equals(outputFilePath, outputFile))
                {
                    File.Delete(outputFilePath);
                    File.Move(outputFile, outputFilePath);
                }
            }
        }

        /// <summary>
        /// Retrieves and exports the materials for the current model, to be used alongside ExportModel
        /// </summary>
        public static async Task ExportMaterialsForModel(TTModel model, string outputFilePath, DirectoryInfo gameDirectory, int mtrlVariant = 1, XivRace targetRace = XivRace.All_Races)
        {
            var modelName = Path.GetFileNameWithoutExtension(model.Source);
            var directory = Path.GetDirectoryName(outputFilePath);

            // Language doesn't actually matter here.
            var _mtrl = new Mtrl(XivCache.GameInfo.GameDirectory);
            var _tex = new Tex(gameDirectory);
            var _index = new Index(gameDirectory);
            var materialIdx = 0;


            foreach (var materialName in model.Materials)
            {
                try
                {
                    var mdlPath = model.Source;

                    // Set source race to match so that it doesn't get replaced
                    if (targetRace != XivRace.All_Races)
                    {
                        var bodyRegex = new Regex("(b[0-9]{4})");
                        var faceRegex = new Regex("(f[0-9]{4})");
                        var tailRegex = new Regex("(t[0-9]{4})");

                        if (bodyRegex.Match(materialName).Success)
                        {
                            var currentRace = model.Source.Substring(model.Source.LastIndexOf('c') + 1, 4);
                            mdlPath = model.Source.Replace(currentRace, targetRace.GetRaceCode());
                        }

                        var faceMatch = faceRegex.Match(materialName);
                        if (faceMatch.Success)
                        {
                            var mdlFace = faceRegex.Match(model.Source).Value;

                            mdlPath = model.Source.Replace(mdlFace, faceMatch.Value);
                        }

                        var tailMatch = tailRegex.Match(materialName);
                        if (tailMatch.Success)
                        {
                            var mdlTail = tailRegex.Match(model.Source).Value;

                            mdlPath = model.Source.Replace(mdlTail, tailMatch.Value);
                        }
                    }

                    // This messy sequence is ultimately to get access to _modelMaps.GetModelMaps().
                    var mtrlPath = _mtrl.GetMtrlPath(mdlPath, materialName, mtrlVariant);
                    var mtrlOffset = await _index.GetDataOffset(mtrlPath);
                    var mtrl = await _mtrl.GetMtrlData(mtrlOffset, mtrlPath, 11);
                    var modelMaps = await ModelTexture.GetModelMaps(gameDirectory, mtrl);

                    // Outgoing file names.
                    var mtrl_prefix = directory + "\\" + Path.GetFileNameWithoutExtension(materialName.Substring(1)) + "_";
                    var mtrl_suffix = ".png";

                    if (modelMaps.Diffuse != null && modelMaps.Diffuse.Length > 0)
                    {
                        using (Image<Rgba32> img = Image.LoadPixelData<Rgba32>(modelMaps.Diffuse, modelMaps.Width, modelMaps.Height))
                        {
                            img.Save(mtrl_prefix + "d" + mtrl_suffix, new PngEncoder());
                        }
                    }

                    if (modelMaps.Normal != null && modelMaps.Diffuse.Length > 0)
                    {
                        using (Image<Rgba32> img = Image.LoadPixelData<Rgba32>(modelMaps.Normal, modelMaps.Width, modelMaps.Height))
                        {
                            img.Save(mtrl_prefix + "n" + mtrl_suffix, new PngEncoder());
                        }
                    }

                    if (modelMaps.Specular != null && modelMaps.Diffuse.Length > 0)
                    {
                        using (Image<Rgba32> img = Image.LoadPixelData<Rgba32>(modelMaps.Specular, modelMaps.Width, modelMaps.Height))
                        {
                            img.Save(mtrl_prefix + "s" + mtrl_suffix, new PngEncoder());
                        }
                    }

                    if (modelMaps.Alpha != null && modelMaps.Diffuse.Length > 0)
                    {
                        using (Image<Rgba32> img = Image.LoadPixelData<Rgba32>(modelMaps.Alpha, modelMaps.Width, modelMaps.Height))
                        {
                            img.Save(mtrl_prefix + "o" + mtrl_suffix, new PngEncoder());
                        }
                    }

                    if (modelMaps.Emissive != null && modelMaps.Diffuse.Length > 0)
                    {
                        using (Image<Rgba32> img = Image.LoadPixelData<Rgba32>(modelMaps.Emissive, modelMaps.Width, modelMaps.Height))
                        {
                            img.Save(mtrl_prefix + "e" + mtrl_suffix, new PngEncoder());
                        }
                    }

                }
                catch (Exception exc)
                {
                    // Failing to resolve a material is considered a non-critical error.
                    // Continue attempting to resolve the rest of the materials in the model.
                    //throw exc;
                }
                materialIdx++;
            }
        }


        /// <summary>
        /// Retreives the high level TTModel representation of an underlying MDL file.
        /// </summary>
        /// <param name="item"></param>
        /// <param name="race"></param>
        /// <param name="submeshId"></param>
        /// <param name="getOriginal"></param>
        /// <returns></returns>
        public async Task<TTModel> GetModel(IItemModel item, XivRace race, string submeshId = null, bool getOriginal = false)
        {
            var index = new Index(_gameDirectory);
            var dat = new Dat(_gameDirectory);
            var modding = new Modding(_gameDirectory);
            var mdl = await GetRawMdlData(item, race, submeshId, getOriginal);
            var ttModel = TTModel.FromRaw(mdl);
            return ttModel;
        }
        public async Task<TTModel> GetModel(string mdlPath, bool getOriginal = false)
        {
            var mdl = await GetRawMdlData(mdlPath, getOriginal);
            var ttModel = TTModel.FromRaw(mdl);
            return ttModel;
        }

        public async Task<XivMdl> GetRawMdlData(IItemModel item, XivRace race, string submeshId = null, bool getOriginal = false)
        {
            var mdlPath = await GetMdlPath(item, race, submeshId);
            return await GetRawMdlData(mdlPath, getOriginal);
        }

        /// <summary>
        /// Retrieves the raw XivMdl file at a given internal file path.
        /// 
        /// If it an explicit offset is provided, it will be used over path or mod offset resolution.
        /// </summary>
        /// <returns>An XivMdl structure containing all mdl data.</returns>
        public async Task<XivMdl> GetRawMdlData(string mdlPath, bool getOriginal = false, long offset = 0)
        {

            var dat = new Dat(_gameDirectory);
            var modding = new Modding(_gameDirectory);
            var mod = await modding.TryGetModEntry(mdlPath);
            var modded = mod != null && mod.enabled;
            var getShapeData = true;


            if (offset == 0)
            {
                var index = new Index(_gameDirectory);
                offset = await index.GetDataOffset(mdlPath);

                if (getOriginal)
                {
                    if (modded)
                    {
                        offset = mod.data.originalOffset;
                        modded = false;
                    }
                }
            }


            if (offset == 0)
            {
                return null;
            }

            var mdlData = await dat.GetType3Data(offset, IOUtil.GetDataFileFromPath(mdlPath));

            var xivMdl = new XivMdl { MdlPath = mdlPath };
            int totalNonNullMaterials = 0;

            using (var br = new BinaryReader(new MemoryStream(mdlData.Data)))
            {
                var version = br.ReadUInt16();
                var val2 = br.ReadUInt16();
                var mdlSignature = 0;

                int mdlVersion = version >= 6 ? 6 : 5;
                xivMdl.MdlVersion = version;

                // We skip the Vertex Data Structures for now
                // This is done so that we can get the correct number of meshes per LoD first
                br.BaseStream.Seek(64 + 136 * mdlData.MeshCount + 4, SeekOrigin.Begin);

                var mdlPathData = new MdlPathData()
                {
                    PathCount = br.ReadInt32(),
                    PathBlockSize = br.ReadInt32(),
                    AttributeList = new List<string>(),
                    BoneList = new List<string>(),
                    MaterialList = new List<string>(),
                    ShapeList = new List<string>(),
                    ExtraPathList = new List<string>()
                };

                // Get the entire path string block to parse later
                // This will be done when we obtain the path counts for each type
                var pathBlock = br.ReadBytes(mdlPathData.PathBlockSize);

                var mdlModelData = new MdlModelData
                {
                    Unknown0 = br.ReadInt32(),
                    MeshCount = br.ReadInt16(),
                    AttributeCount = br.ReadInt16(),
                    MeshPartCount = br.ReadInt16(),
                    MaterialCount = br.ReadInt16(),
                    BoneCount = br.ReadInt16(),
                    BoneListCount = br.ReadInt16(),
                    ShapeCount = br.ReadInt16(),
                    ShapePartCount = br.ReadInt16(),
                    ShapeDataCount = br.ReadUInt16(),
                    LoDCount = br.ReadByte(),
                    Unknown1 = br.ReadByte(),
                    Unknown2 = br.ReadInt16(),
                    Unknown3 = br.ReadInt16(),
                    Unknown4 = br.ReadInt16(),
                    Unknown5 = br.ReadInt16(),
                    Unknown6 = br.ReadInt16(),
                    Unknown7 = br.ReadInt16(),
                    Unknown8 = br.ReadInt16(), // Used for transform count with furniture
                    Unknown9 = br.ReadInt16(),
                    Unknown10a = br.ReadByte(),
                    Unknown10b = br.ReadByte(),
                    Unknown11 = br.ReadInt16(),
                    Unknown12 = br.ReadInt16(),
                    Unknown13 = br.ReadInt16(),
                    Unknown14 = br.ReadInt16(),
                    Unknown15 = br.ReadInt16(),
                    Unknown16 = br.ReadInt16(),
                    Unknown17 = br.ReadInt16()
                };

                // Finished reading all MdlModelData
                // Adding to xivMdl
                xivMdl.ModelData = mdlModelData;

                // Now that we have the path counts wee can parse the path strings
                using (var br1 = new BinaryReader(new MemoryStream(pathBlock)))
                {
                    // Attribute Paths
                    for (var i = 0; i < mdlModelData.AttributeCount; i++)
                    {
                        // Because we don't know the length of the string, we read the data until we reach a 0 value
                        // That 0 value is the space between strings
                        byte a;
                        var atrName = new List<byte>();
                        while ((a = br1.ReadByte()) != 0)
                        {
                            atrName.Add(a);
                        }

                        // Read the string from the byte array and remove null terminators
                        var atr = Encoding.ASCII.GetString(atrName.ToArray()).Replace("\0", "");

                        // Add the attribute to the list
                        mdlPathData.AttributeList.Add(atr);
                    }

                    // Bone Paths
                    for (var i = 0; i < mdlModelData.BoneCount; i++)
                    {
                        byte a;
                        var boneName = new List<byte>();
                        while ((a = br1.ReadByte()) != 0)
                        {
                            boneName.Add(a);
                        }

                        var bone = Encoding.ASCII.GetString(boneName.ToArray()).Replace("\0", "");

                        mdlPathData.BoneList.Add(bone);
                    }

                    // Material Paths
                    for (var i = 0; i < mdlModelData.MaterialCount; i++)
                    {
                        byte a;
                        var materialName = new List<byte>();
                        while ((a = br1.ReadByte()) != 0)
                        {
                            materialName.Add(a);
                        }

                        var mat = Encoding.ASCII.GetString(materialName.ToArray()).Replace("\0", "");
                        if (mat.StartsWith("shp_"))
                        {
                            // Catch case for situation where there's null values at the end of the materials list.
                            mdlPathData.ShapeList.Add(mat);
                        }
                        else
                        {
                            totalNonNullMaterials++;
                            mdlPathData.MaterialList.Add(mat);
                        }
                    }


                    // Shape Paths
                    for (var i = 0; i < mdlModelData.ShapeCount; i++)
                    {
                        byte a;
                        var shapeName = new List<byte>();
                        while ((a = br1.ReadByte()) != 0)
                        {
                            shapeName.Add(a);
                        }

                        var shp = Encoding.ASCII.GetString(shapeName.ToArray()).Replace("\0", "");

                        mdlPathData.ShapeList.Add(shp);
                    }

                    var remainingPathData = mdlPathData.PathBlockSize - br1.BaseStream.Position;
                    if (remainingPathData > 2)
                    {
                        while (remainingPathData != 0)
                        {
                            byte a;
                            var extraName = new List<byte>();
                            while ((a = br1.ReadByte()) != 0)
                            {
                                extraName.Add(a);
                                remainingPathData--;
                            }

                            remainingPathData--;

                            if (extraName.Count > 0)
                            {
                                var extra = Encoding.ASCII.GetString(extraName.ToArray()).Replace("\0", "");

                                mdlPathData.ExtraPathList.Add(extra);
                            }
                        }

                    }
                }

                // Finished reading all Path Data
                // Adding to xivMdl
                xivMdl.PathData = mdlPathData;

                // Currently Unknown Data
                var unkData0 = new UnknownData0
                {
                    Unknown = br.ReadBytes(mdlModelData.Unknown2 * 32)
                };

                // Finished reading all UnknownData0
                // Adding to xivMdl
                xivMdl.UnkData0 = unkData0;

                var totalLoDMeshes = 0;

                // We add each LoD to the list
                // Note: There is always 3 LoD
                xivMdl.LoDList = new List<LevelOfDetail>();
                for (var i = 0; i < 3; i++)
                {
                    var lod = new LevelOfDetail
                    {
                        MeshOffset = br.ReadUInt16(),
                        MeshCount = br.ReadInt16(),
                        Unknown0 = br.ReadInt32(),
                        Unknown1 = br.ReadInt32(),
                        MeshEnd = br.ReadInt16(),
                        ExtraMeshCount = br.ReadInt16(),
                        MeshSum = br.ReadInt16(),
                        Unknown2 = br.ReadInt16(),
                        Unknown3 = br.ReadInt32(),
                        Unknown4 = br.ReadInt32(),
                        Unknown5 = br.ReadInt32(),
                        IndexDataStart = br.ReadInt32(),
                        Unknown6 = br.ReadInt32(),
                        Unknown7 = br.ReadInt32(),
                        VertexDataSize = br.ReadInt32(),
                        IndexDataSize = br.ReadInt32(),
                        VertexDataOffset = br.ReadInt32(),
                        IndexDataOffset = br.ReadInt32(),
                        MeshDataList = new List<MeshData>()
                    };
                    // Finished reading LoD

                    totalLoDMeshes += lod.MeshCount;

                    // if LoD0 shows no mesh, add one (This is rare, but happens on company chest for example)
                    if (i == 0 && lod.MeshCount == 0)
                    {
                        lod.MeshCount = 1;
                    }

                    // This is a simple check to identify old mods that may have broken shape data.
                    // Old mods still have LoD 1+ data.
                    if (modded  && i > 0 && lod.MeshCount > 0)
                    {
                        getShapeData = false;
                    }

                    //Adding to xivMdl
                    xivMdl.LoDList.Add(lod);
                }

                //HACK: This is a workaround for certain furniture items, mainly with picture frames and easel
                var isEmpty = false;
                try
                {
                    isEmpty = br.PeekChar() == 0;
                }
                catch { }

                if (isEmpty && totalLoDMeshes < mdlModelData.MeshCount)
                {
                    xivMdl.ExtraLoDList = new List<LevelOfDetail>();

                    for (var i = 0; i < mdlModelData.Unknown10a; i++)
                    {
                        var lod = new LevelOfDetail
                        {
                            MeshOffset = br.ReadUInt16(),
                            MeshCount = br.ReadInt16(),
                            Unknown0 = br.ReadInt32(),
                            Unknown1 = br.ReadInt32(),
                            MeshEnd = br.ReadInt16(),
                            ExtraMeshCount = br.ReadInt16(),
                            MeshSum = br.ReadInt16(),
                            Unknown2 = br.ReadInt16(),
                            Unknown3 = br.ReadInt32(),
                            Unknown4 = br.ReadInt32(),
                            Unknown5 = br.ReadInt32(),
                            IndexDataStart = br.ReadInt32(),
                            Unknown6 = br.ReadInt32(),
                            Unknown7 = br.ReadInt32(),
                            VertexDataSize = br.ReadInt32(),
                            IndexDataSize = br.ReadInt32(),
                            VertexDataOffset = br.ReadInt32(),
                            IndexDataOffset = br.ReadInt32(),
                            MeshDataList = new List<MeshData>()
                        };

                        xivMdl.ExtraLoDList.Add(lod);
                    }
                }


                // Now that we have the LoD data, we can go back and read the Vertex Data Structures
                // First we save our current position
                var savePosition = br.BaseStream.Position;

                var loDStructPos = 68;
                // for each mesh in each lod
                for (var i = 0; i < xivMdl.LoDList.Count; i++)
                {
                    var totalMeshCount = xivMdl.LoDList[i].MeshCount + xivMdl.LoDList[i].ExtraMeshCount;
                    for (var j = 0; j < totalMeshCount; j++)
                    {
                        xivMdl.LoDList[i].MeshDataList.Add(new MeshData());
                        xivMdl.LoDList[i].MeshDataList[j].VertexDataStructList = new List<VertexDataStruct>();

                        // LoD Index * Vertex Data Structure size + Header

                        br.BaseStream.Seek(j * 136 + loDStructPos, SeekOrigin.Begin);

                        // If the first byte is 255, we reached the end of the Vertex Data Structs
                        var dataBlockNum = br.ReadByte();
                        while (dataBlockNum != 255)
                        {
                            var vertexDataStruct = new VertexDataStruct
                            {
                                DataBlock = dataBlockNum,
                                DataOffset = br.ReadByte(),
                                DataType = VertexTypeDictionary[br.ReadByte()],
                                DataUsage = VertexUsageDictionary[br.ReadByte()]
                            };

                            xivMdl.LoDList[i].MeshDataList[j].VertexDataStructList.Add(vertexDataStruct);

                            // padding between Vertex Data Structs
                            br.ReadBytes(4);

                            dataBlockNum = br.ReadByte();
                        }
                    }

                    loDStructPos += 136 * xivMdl.LoDList[i].MeshCount;
                }

                // Now that we finished reading the Vertex Data Structures, we can go back to our saved position
                br.BaseStream.Seek(savePosition, SeekOrigin.Begin);

                // Mesh Data Information
                var meshNum = 0;
                foreach (var lod in xivMdl.LoDList)
                {
                    var totalMeshCount = lod.MeshCount + lod.ExtraMeshCount;

                    for (var i = 0; i < totalMeshCount; i++)
                    {
                        var meshDataInfo = new MeshDataInfo
                        {
                            VertexCount = br.ReadInt32(),
                            IndexCount = br.ReadInt32(),
                            MaterialIndex = br.ReadInt16(),
                            MeshPartIndex = br.ReadInt16(),
                            MeshPartCount = br.ReadInt16(),
                            BoneSetIndex = br.ReadInt16(),
                            IndexDataOffset = br.ReadInt32(),
                            VertexDataOffset0 = br.ReadInt32(),
                            VertexDataOffset1 = br.ReadInt32(),
                            VertexDataOffset2 = br.ReadInt32(),
                            VertexDataEntrySize0 = br.ReadByte(),
                            VertexDataEntrySize1 = br.ReadByte(),
                            VertexDataEntrySize2 = br.ReadByte(),
                            VertexDataBlockCount = br.ReadByte()
                        };

                        lod.MeshDataList[i].MeshInfo = meshDataInfo;

                        // In the event we have a null material reference, set it to material 0 to be safe.
                        if (meshDataInfo.MaterialIndex >= totalNonNullMaterials)
                        {
                            meshDataInfo.MaterialIndex = 0;
                        }

                        var materialString = xivMdl.PathData.MaterialList[meshDataInfo.MaterialIndex];
                        // Try block to cover odd cases like Au Ra Male Face #92 where for some reason the
                        // Last LoD points to using a shp for a material for some reason.
                        try
                        {
                            var typeChar = materialString[4].ToString() + materialString[9].ToString();

                            if (typeChar.Equals("cb"))
                            {
                                lod.MeshDataList[i].IsBody = true;
                            }
                        }
                        catch (Exception e)
                        {

                        }

                        meshNum++;
                    }
                }

                // Data block for attributes offset paths
                var attributeDataBlock = new AttributeDataBlock
                {
                    AttributePathOffsetList = new List<int>(xivMdl.ModelData.AttributeCount)
                };

                for (var i = 0; i < xivMdl.ModelData.AttributeCount; i++)
                {
                    attributeDataBlock.AttributePathOffsetList.Add(br.ReadInt32());
                }

                xivMdl.AttrDataBlock = attributeDataBlock;

                // Unknown data block
                // This is commented out to allow housing items to display, the data does not exist for housing items
                // more investigation needed as to what this data is
                var unkData1 = new UnknownData1
                {
                    //Unknown = br.ReadBytes(xivMdl.ModelData.Unknown3 * 20)
                };
                xivMdl.UnkData1 = unkData1;

                // Mesh Parts
                foreach (var lod in xivMdl.LoDList)
                {
                    foreach (var meshData in lod.MeshDataList)
                    {
                        meshData.MeshPartList = new List<MeshPart>();

                        for (var i = 0; i < meshData.MeshInfo.MeshPartCount; i++)
                        {
                            var meshPart = new MeshPart
                            {
                                IndexOffset = br.ReadInt32(),
                                IndexCount = br.ReadInt32(),
                                AttributeBitmask = br.ReadUInt32(),
                                BoneStartOffset = br.ReadInt16(),
                                BoneCount = br.ReadInt16()
                            };

                            meshData.MeshPartList.Add(meshPart);
                        }
                    }
                }

                // Unknown data block
                var unkData2 = new UnknownData2
                {
                    Unknown = br.ReadBytes(xivMdl.ModelData.Unknown9 * 12)
                };
                xivMdl.UnkData2 = unkData2;

                // Data block for materials
                // Currently unknown usage
                var matDataBlock = new MaterialDataBlock
                {
                    MaterialPathOffsetList = new List<int>(xivMdl.ModelData.MaterialCount)
                };

                for (var i = 0; i < xivMdl.ModelData.MaterialCount; i++)
                {
                    matDataBlock.MaterialPathOffsetList.Add(br.ReadInt32());
                }

                xivMdl.MatDataBlock = matDataBlock;

                // Data block for bones
                // Currently unknown usage
                var boneDataBlock = new BoneDataBlock
                {
                    BonePathOffsetList = new List<int>(xivMdl.ModelData.BoneCount)
                };

                for (var i = 0; i < xivMdl.ModelData.BoneCount; i++)
                {
                    boneDataBlock.BonePathOffsetList.Add(br.ReadInt32());
                }

                xivMdl.BoneDataBlock = boneDataBlock;

                // Bone Lists
                xivMdl.MeshBoneSets = new List<BoneSet>();
                if (mdlVersion >= 6) // Mdl Version 6
                {
                    var boneIndexMetaTable = new List<short[]>();

                    for (var i = 0; i < xivMdl.ModelData.BoneListCount; ++i)
                    {
                        boneIndexMetaTable.Add(new short[2] { br.ReadInt16(), br.ReadInt16() });
                    }

                    for (var i = 0; i < xivMdl.ModelData.BoneListCount; ++i)
                    {
                        var boneCount = boneIndexMetaTable[i][1];
                        var boneIndexMesh = new BoneSet
                        {
                            BoneIndices = new List<short>(boneCount)
                        };

                        for (var j = 0; j < boneCount; j++)
                        {
                            boneIndexMesh.BoneIndices.Add(br.ReadInt16());
                        }

                        // Eat another value for alignment to 4 bytes
                        if (boneCount % 2 == 1)
                            br.ReadInt16();

                        boneIndexMesh.BoneIndexCount = boneCount;

                        xivMdl.MeshBoneSets.Add(boneIndexMesh);
                    }
                }
                else // Mdl Version 5
                {
                    for (var i = 0; i < xivMdl.ModelData.BoneListCount; i++)
                    {
                        var boneIndexMesh = new BoneSet
                        {
                            BoneIndices = new List<short>(64)
                        };

                        for (var j = 0; j < 64; j++)
                        {
                            boneIndexMesh.BoneIndices.Add(br.ReadInt16());
                        }

                        boneIndexMesh.BoneIndexCount = br.ReadInt32();

                        xivMdl.MeshBoneSets.Add(boneIndexMesh);
                    }
                }

                var shapeDataLists = new ShapeData
                {
                    ShapeInfoList = new List<ShapeData.ShapeInfo>(),
                    ShapeParts = new List<ShapeData.ShapePart>(),
                    ShapeDataList = new List<ShapeData.ShapeDataEntry>()
                };

                var totalPartCount = 0;
                // Shape Info

                // Each shape has a header entry, then a per-lod entry.
                for (var i = 0; i < xivMdl.ModelData.ShapeCount; i++)
                {

                    // Header - Offset to the shape name.
                    var shapeInfo = new ShapeData.ShapeInfo
                    {
                        ShapeNameOffset = br.ReadInt32(),
                        Name = xivMdl.PathData.ShapeList[i],
                        ShapeLods = new List<ShapeData.ShapeLodInfo>()
                    };

                    // Per LoD entry (offset to this shape's parts in the shape set)
                    var dataInfoIndexList = new List<ushort>();
                    for (var j = 0; j < xivMdl.LoDList.Count; j++)
                    {
                        dataInfoIndexList.Add(br.ReadUInt16());
                    }

                    // Per LoD entry (number of parts in the shape set)
                    var infoPartCountList = new List<short>();
                    for (var j = 0; j < xivMdl.LoDList.Count; j++)
                    {
                        infoPartCountList.Add(br.ReadInt16());
                    }

                    for (var j = 0; j < xivMdl.LoDList.Count; j++)
                    {
                        var shapeIndexPart = new ShapeData.ShapeLodInfo
                        {
                            PartOffset = dataInfoIndexList[j],
                            PartCount = infoPartCountList[j]
                        };
                        shapeInfo.ShapeLods.Add(shapeIndexPart);
                        totalPartCount += shapeIndexPart.PartCount;
                    }

                    shapeDataLists.ShapeInfoList.Add(shapeInfo);
                }

                // Shape Index Info
                for (var i = 0; i < xivMdl.ModelData.ShapePartCount; i++)
                {
                    var shapeIndexInfo = new ShapeData.ShapePart
                    {
                        MeshIndexOffset = br.ReadInt32(),  // The offset to the index block this Shape Data should be replacing in. -- This is how Shape Data is tied to each mesh.
                        IndexCount = br.ReadInt32(),  // # of triangle indices to replace.
                        ShapeDataOffset = br.ReadInt32()   // The offset where this part should start reading in the Shape Data list.
                    };

                    shapeDataLists.ShapeParts.Add(shapeIndexInfo);
                }

                // Shape data
                for (var i = 0; i < xivMdl.ModelData.ShapeDataCount; i++)
                {
                    var shapeData = new ShapeData.ShapeDataEntry
                    {
                        BaseIndex = br.ReadUInt16(),  // Base Triangle Index we're replacing
                        ShapeVertex = br.ReadUInt16()  // The Vertex that Triangle Index should now point to instead.
                    };
                    shapeDataLists.ShapeDataList.Add(shapeData);
                }

                xivMdl.MeshShapeData = shapeDataLists;

                // Build the list of offsets so we can match it for shape data.
                var indexOffsets = new List<List<int>>();
                for (int l = 0; l < xivMdl.LoDList.Count; l++)
                {
                    indexOffsets.Add(new List<int>());
                    for (int m = 0; m < xivMdl.LoDList[l].MeshDataList.Count; m++)
                    {
                        indexOffsets[l].Add(xivMdl.LoDList[l].MeshDataList[m].MeshInfo.IndexDataOffset);
                    }

                }
                xivMdl.MeshShapeData.AssignMeshAndLodNumbers(indexOffsets);

                // Sets the boolean flag if the model has shape data
                xivMdl.HasShapeData = xivMdl.ModelData.ShapeCount > 0 && getShapeData;

                // Bone index for Parts
                var partBoneSet = new BoneSet
                {
                    BoneIndexCount = br.ReadInt32(),
                    BoneIndices = new List<short>()
                };

                for (var i = 0; i < partBoneSet.BoneIndexCount / 2; i++)
                {
                    partBoneSet.BoneIndices.Add(br.ReadInt16());
                }

                xivMdl.PartBoneSets = partBoneSet;

                // Padding
                xivMdl.PaddingSize = br.ReadByte();
                xivMdl.PaddedBytes = br.ReadBytes(xivMdl.PaddingSize);

                // Bounding box
                var boundingBox = new BoundingBox
                {
                    PointList = new List<Vector4>()
                };

                for (var i = 0; i < 8; i++)
                {
                    boundingBox.PointList.Add(new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
                }

                xivMdl.BoundBox = boundingBox;

                // Bone Transform Data
                xivMdl.BoneTransformDataList = new List<BoneTransformData>();

                var transformCount = xivMdl.ModelData.BoneCount;

                if (transformCount == 0)
                {
                    transformCount = xivMdl.ModelData.Unknown8;
                }

                for (var i = 0; i < transformCount; i++)
                {
                    var boneTransformData = new BoneTransformData
                    {
                        Transform0 = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                        Transform1 = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle())
                    };

                    xivMdl.BoneTransformDataList.Add(boneTransformData);
                }

                var lodNum = 0;
                var totalMeshNum = 0;
                foreach (var lod in xivMdl.LoDList)
                {
                    if (lod.MeshCount == 0) continue;

                    var meshDataList = lod.MeshDataList;

                    if (lod.MeshOffset != totalMeshNum)
                    {
                        meshDataList = xivMdl.LoDList[lodNum + 1].MeshDataList;
                    }

                    foreach (var meshData in meshDataList)
                    {
                        var vertexData = new VertexData
                        {
                            Positions = new Vector3Collection(),
                            BoneWeights = new List<float[]>(),
                            BoneIndices = new List<byte[]>(),
                            Normals = new Vector3Collection(),
                            BiNormals = new Vector3Collection(),
                            BiNormalHandedness = new List<byte>(),
                            Tangents = new Vector3Collection(),
                            Colors = new List<SharpDX.Color>(),
                            Colors4 = new Color4Collection(),
                            TextureCoordinates0 = new Vector2Collection(),
                            TextureCoordinates1 = new Vector2Collection(),
                            Indices = new IntCollection()
                        };

                        #region Positions
                        // Get the Vertex Data Structure for positions
                        var posDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                             where vertexDataStruct.DataUsage == VertexUsageType.Position
                                             select vertexDataStruct).FirstOrDefault();

                        int vertexDataOffset;
                        int vertexDataSize;

                        if (posDataStruct != null)
                        {
                            // Determine which data block the position data is in
                            // This always seems to be in the first data block
                            switch (posDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                // Get the offset for the position data for each vertex
                                var positionOffset = lod.VertexDataOffset + vertexDataOffset + posDataStruct.DataOffset + vertexDataSize * i;

                                // Go to the Data Block
                                br.BaseStream.Seek(positionOffset, SeekOrigin.Begin);

                                Vector3 positionVector;
                                // Position data is either stored in half-floats or singles
                                if (posDataStruct.DataType == VertexDataType.Half4)
                                {
                                    var x = new SharpDX.Half(br.ReadUInt16());
                                    var y = new SharpDX.Half(br.ReadUInt16());
                                    var z = new SharpDX.Half(br.ReadUInt16());
                                    var w = new SharpDX.Half(br.ReadUInt16());

                                    positionVector = new Vector3(x, y, z);
                                }
                                else
                                {
                                    var x = br.ReadSingle();
                                    var y = br.ReadSingle();
                                    var z = br.ReadSingle();

                                    positionVector = new Vector3(x, y, z);
                                }
                                vertexData.Positions.Add(positionVector);
                            }
                        }

                        #endregion


                        #region BoneWeights

                        // Get the Vertex Data Structure for bone weights
                        var bwDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                            where vertexDataStruct.DataUsage == VertexUsageType.BoneWeight
                                            select vertexDataStruct).FirstOrDefault();

                        if (bwDataStruct != null)
                        {
                            // Determine which data block the bone weight data is in
                            // This always seems to be in the first data block
                            switch (bwDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of bone weights per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var bwOffset = lod.VertexDataOffset + vertexDataOffset + bwDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(bwOffset, SeekOrigin.Begin);
                                var b0 = br.ReadByte();
                                var b1 = br.ReadByte();
                                var b2 = br.ReadByte();
                                var b3 = br.ReadByte();

                                var bw0 = b0 / 255f;
                                var bw1 = b1 / 255f;
                                var bw2 = b2 / 255f;
                                var bw3 = b3 / 255f;

                                vertexData.BoneWeights.Add(new[] { bw0, bw1, bw2, bw3 });
                            }
                        }


                        #endregion


                        #region BoneIndices

                        // Get the Vertex Data Structure for bone indices
                        var biDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                            where vertexDataStruct.DataUsage == VertexUsageType.BoneIndex
                                            select vertexDataStruct).FirstOrDefault();

                        if (biDataStruct != null)
                        {
                            // Determine which data block the bone index data is in
                            // This always seems to be in the first data block
                            switch (biDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of bone indices per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var biOffset = lod.VertexDataOffset + vertexDataOffset + biDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(biOffset, SeekOrigin.Begin);

                                var bi0 = br.ReadByte();
                                var bi1 = br.ReadByte();
                                var bi2 = br.ReadByte();
                                var bi3 = br.ReadByte();

                                vertexData.BoneIndices.Add(new[] { bi0, bi1, bi2, bi3 });
                            }
                        }

                        #endregion


                        #region Normals

                        // Get the Vertex Data Structure for Normals
                        var normDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                              where vertexDataStruct.DataUsage == VertexUsageType.Normal
                                              select vertexDataStruct).FirstOrDefault();

                        if (normDataStruct != null)
                        {
                            // Determine which data block the normal data is in
                            // This always seems to be in the second data block
                            switch (normDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of normals per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var normOffset = lod.VertexDataOffset + vertexDataOffset + normDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(normOffset, SeekOrigin.Begin);

                                Vector3 normalVector;
                                // Normal data is either stored in half-floats or singles
                                if (normDataStruct.DataType == VertexDataType.Half4)
                                {
                                    var x = new SharpDX.Half(br.ReadUInt16());
                                    var y = new SharpDX.Half(br.ReadUInt16());
                                    var z = new SharpDX.Half(br.ReadUInt16());
                                    var w = new SharpDX.Half(br.ReadUInt16());

                                    normalVector = new Vector3(x, y, z);
                                }
                                else
                                {
                                    var x = br.ReadSingle();
                                    var y = br.ReadSingle();
                                    var z = br.ReadSingle();

                                    normalVector = new Vector3(x, y, z);
                                }

                                vertexData.Normals.Add(normalVector);
                            }
                        }

                        #endregion


                        #region BiNormals

                        // Get the Vertex Data Structure for BiNormals
                        var biNormDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                                where vertexDataStruct.DataUsage == VertexUsageType.Binormal
                                                select vertexDataStruct).FirstOrDefault();

                        if (biNormDataStruct != null)
                        {
                            // Determine which data block the binormal data is in
                            // This always seems to be in the second data block
                            switch (biNormDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of biNormals per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var biNormOffset = lod.VertexDataOffset + vertexDataOffset + biNormDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(biNormOffset, SeekOrigin.Begin);

                                var x = br.ReadByte() * 2 / 255f - 1f;
                                var y = br.ReadByte() * 2 / 255f - 1f;
                                var z = br.ReadByte() * 2 / 255f - 1f;
                                var w = br.ReadByte();

                                vertexData.BiNormals.Add(new Vector3(x, y, z));
                                vertexData.BiNormalHandedness.Add(w);
                            }
                        }

                        #endregion

                        #region Tangents

                        // Get the Vertex Data Structure for Tangents
                        var tangentDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                                 where vertexDataStruct.DataUsage == VertexUsageType.Tangent
                                                 select vertexDataStruct).FirstOrDefault();

                        if (tangentDataStruct != null)
                        {
                            // Determine which data block the tangent data is in
                            // This always seems to be in the second data block
                            switch (tangentDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is one set of tangents per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var tangentOffset = lod.VertexDataOffset + vertexDataOffset + tangentDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(tangentOffset, SeekOrigin.Begin);

                                var x = br.ReadByte() * 2 / 255f - 1f;
                                var y = br.ReadByte() * 2 / 255f - 1f;
                                var z = br.ReadByte() * 2 / 255f - 1f;
                                var w = br.ReadByte();

                                vertexData.Tangents.Add(new Vector3(x, y, z));
                                //vertexData.TangentHandedness.Add(w);
                            }
                        }

                        #endregion


                        #region VertexColor

                        // Get the Vertex Data Structure for colors
                        var colorDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                               where vertexDataStruct.DataUsage == VertexUsageType.Color
                                               select vertexDataStruct).FirstOrDefault();

                        if (colorDataStruct != null)
                        {
                            // Determine which data block the color data is in
                            // This always seems to be in the second data block
                            switch (colorDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;

                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of colors per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var colorOffset = lod.VertexDataOffset + vertexDataOffset + colorDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(colorOffset, SeekOrigin.Begin);

                                var r = br.ReadByte();
                                var g = br.ReadByte();
                                var b = br.ReadByte();
                                var a = br.ReadByte();

                                vertexData.Colors.Add(new SharpDX.Color(r, g, b, a));
                                vertexData.Colors4.Add(new Color4((r / 255f), (g / 255f), (b / 255f), (a / 255f)));
                            }
                        }

                        #endregion


                        #region TextureCoordinates

                        // Get the Vertex Data Structure for texture coordinates
                        var tcDataStruct = (from vertexDataStruct in meshData.VertexDataStructList
                                            where vertexDataStruct.DataUsage == VertexUsageType.TextureCoordinate
                                            select vertexDataStruct).FirstOrDefault();

                        if (tcDataStruct != null)
                        {
                            // Determine which data block the texture coordinate data is in
                            // This always seems to be in the second data block
                            switch (tcDataStruct.DataBlock)
                            {
                                case 0:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset0;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize0;
                                    break;
                                case 1:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset1;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize1;
                                    break;
                                default:
                                    vertexDataOffset = meshData.MeshInfo.VertexDataOffset2;
                                    vertexDataSize = meshData.MeshInfo.VertexDataEntrySize2;
                                    break;
                            }

                            // There is always one set of texture coordinates per vertex
                            for (var i = 0; i < meshData.MeshInfo.VertexCount; i++)
                            {
                                var tcOffset = lod.VertexDataOffset + vertexDataOffset + tcDataStruct.DataOffset + vertexDataSize * i;

                                br.BaseStream.Seek(tcOffset, SeekOrigin.Begin);

                                Vector2 tcVector1;
                                Vector2 tcVector2;
                                // Normal data is either stored in half-floats or singles
                                if (tcDataStruct.DataType == VertexDataType.Half4)
                                {
                                    var x = new SharpDX.Half(br.ReadUInt16());
                                    var y = new SharpDX.Half(br.ReadUInt16());
                                    var x1 = new SharpDX.Half(br.ReadUInt16());
                                    var y1 = new SharpDX.Half(br.ReadUInt16());

                                    tcVector1 = new Vector2(x, y);
                                    tcVector2 = new Vector2(x1, y1);


                                    vertexData.TextureCoordinates0.Add(tcVector1);
                                    vertexData.TextureCoordinates1.Add(tcVector2);
                                }
                                else if (tcDataStruct.DataType == VertexDataType.Half2)
                                {
                                    var x = new SharpDX.Half(br.ReadUInt16());
                                    var y = new SharpDX.Half(br.ReadUInt16());

                                    tcVector1 = new Vector2(x, y);

                                    vertexData.TextureCoordinates0.Add(tcVector1);
                                }
                                else if (tcDataStruct.DataType == VertexDataType.Float2)
                                {
                                    var x = br.ReadSingle();
                                    var y = br.ReadSingle();

                                    tcVector1 = new Vector2(x, y);
                                    vertexData.TextureCoordinates0.Add(tcVector1);
                                }
                                else if (tcDataStruct.DataType == VertexDataType.Float4)
                                {
                                    var x = br.ReadSingle();
                                    var y = br.ReadSingle();
                                    var x1 = br.ReadSingle();
                                    var y1 = br.ReadSingle();

                                    tcVector1 = new Vector2(x, y);
                                    tcVector2 = new Vector2(x1, y1);


                                    vertexData.TextureCoordinates0.Add(tcVector1);
                                    vertexData.TextureCoordinates1.Add(tcVector2);
                                }

                            }
                        }

                        #endregion

                        #region Indices

                        var indexOffset = lod.IndexDataOffset + meshData.MeshInfo.IndexDataOffset * 2;

                        br.BaseStream.Seek(indexOffset, SeekOrigin.Begin);

                        for (var i = 0; i < meshData.MeshInfo.IndexCount; i++)
                        {
                            vertexData.Indices.Add(br.ReadUInt16());
                        }

                        #endregion

                        meshData.VertexData = vertexData;
                        totalMeshNum++;
                    }

                    #region MeshShape

                    // If the model contains Shape Data, parse the data for each mesh
                    if (xivMdl.HasShapeData && getShapeData)
                    {
                        //Dictionary containing <index data offset, mesh number>
                        var indexMeshNum = new Dictionary<int, int>();

                        var shapeData = xivMdl.MeshShapeData.ShapeDataList;

                        // Get the index data offsets in each mesh
                        for (var i = 0; i < lod.MeshCount; i++)
                        {
                            var indexDataOffset = lod.MeshDataList[i].MeshInfo.IndexDataOffset;

                            if (!indexMeshNum.ContainsKey(indexDataOffset))
                            {
                                indexMeshNum.Add(indexDataOffset, i);
                            }
                        }

                        for (var i = 0; i < lod.MeshCount; i++)
                        {
                            var referencePositionsDictionary = new Dictionary<int, Vector3>();
                            var meshShapePositionsDictionary = new SortedDictionary<int, Vector3>();
                            var shapeIndexOffsetDictionary = new Dictionary<int, Dictionary<ushort, ushort>>();

                            // Shape info list
                            var shapeInfoList = xivMdl.MeshShapeData.ShapeInfoList;

                            // Number of shape info in each mesh
                            var perMeshCount = xivMdl.ModelData.ShapeCount;

                            for (var j = 0; j < perMeshCount; j++)
                            {
                                var shapeInfo = shapeInfoList[j];

                                var indexPart = shapeInfo.ShapeLods[lodNum];

                                // The part count
                                var infoPartCount = indexPart.PartCount;

                                for (var k = 0; k < infoPartCount; k++)
                                {
                                    // Gets the data info for the part
                                    var shapeDataInfo = xivMdl.MeshShapeData.ShapeParts[indexPart.PartOffset + k];

                                    // The offset in the shape data 
                                    var indexDataOffset = shapeDataInfo.MeshIndexOffset;

                                    var indexMeshLocation = 0;

                                    // Determine which mesh the info belongs to
                                    if (indexMeshNum.ContainsKey(indexDataOffset))
                                    {
                                        indexMeshLocation = indexMeshNum[indexDataOffset];

                                        // Move to the next part if it is not the current mesh
                                        if (indexMeshLocation != i)
                                        {
                                            continue;
                                        }
                                    }

                                    // Get the mesh data
                                    var mesh = lod.MeshDataList[indexMeshLocation];

                                    // Get the shape data for the current mesh
                                    var shapeDataForMesh = shapeData.GetRange(shapeDataInfo.ShapeDataOffset, shapeDataInfo.IndexCount);

                                    // Fill shape data dictionaries
                                    ushort dataIndex = ushort.MaxValue;
                                    foreach (var data in shapeDataForMesh)
                                    {
                                        var referenceIndex = 0;

                                        if (data.BaseIndex < mesh.VertexData.Indices.Count)
                                        {
                                            // Gets the index to which the data is referencing
                                            referenceIndex = mesh.VertexData.Indices[data.BaseIndex];

                                            //throw new Exception($"Reference Index is larger than the index count. Reference Index: {data.ReferenceIndexOffset}  Index Count: {mesh.VertexData.Indices.Count}");
                                        }

                                        if (!referencePositionsDictionary.ContainsKey(data.BaseIndex))
                                        {
                                            if (mesh.VertexData.Positions.Count > referenceIndex)
                                            {
                                                referencePositionsDictionary.Add(data.BaseIndex, mesh.VertexData.Positions[referenceIndex]);
                                            }
                                        }

                                        if (!meshShapePositionsDictionary.ContainsKey(data.ShapeVertex))
                                        {
                                            if (data.ShapeVertex >= mesh.VertexData.Positions.Count)
                                            {
                                                meshShapePositionsDictionary.Add(data.ShapeVertex, new Vector3(0));
                                            }
                                            else
                                            {
                                                meshShapePositionsDictionary.Add(data.ShapeVertex, mesh.VertexData.Positions[data.ShapeVertex]);
                                            }
                                        }
                                    }

                                    if (mesh.ShapePathList != null)
                                    {
                                        mesh.ShapePathList.Add(shapeInfo.Name);
                                    }
                                    else
                                    {
                                        mesh.ShapePathList = new List<string> { shapeInfo.Name };
                                    }
                                }
                            }
                        }
                    }

                    lodNum++;

                    #endregion
                }
            }

            return xivMdl;
        }


        /// <summary>
        /// Extracts and calculates the full MTRL paths from a given MDL file.
        /// A material variant of -1 gets the materials for ALL variants,
        /// effectively generating the 'child files' list for an Mdl file.
        /// </summary>
        /// <param name="mdlPath"></param>
        /// <param name="getOriginal"></param>
        /// <returns></returns>
        public async Task<List<string>> GetReferencedMaterialPaths(string mdlPath, int materialVariant = -1, bool getOriginal = false, bool includeSkin = true, IndexFile index = null, ModList modlist = null)
        {
            // Language is irrelevant here.
            var dataFile = IOUtil.GetDataFileFromPath(mdlPath);
            var _mtrl = new Mtrl(XivCache.GameInfo.GameDirectory);
            var _imc = new Imc(_gameDirectory);
            var useCached = true;
            if (index == null)
            {
                useCached = false;
                var _index = new Index(_gameDirectory);
                var _modding = new Modding(_gameDirectory);
                index = await _index.GetIndexFile(dataFile, false, true);
                modlist = await _modding.GetModListAsync();
            }

            var materials = new List<string>();

            // Read the raw Material names from the file.
            var materialNames = await GetReferencedMaterialNames(mdlPath, getOriginal, index, modlist);
            var root = await XivCache.GetFirstRoot(mdlPath);
            if(materialNames.Count == 0)
            {
                return materials;
            }

            var materialVariants = new HashSet<int>();
            if (materialVariant >= 0)
            {
                // If we had a specific variant to get, just use that.
                materialVariants.Add(materialVariant);

            }
            else if(useCached && root != null)
            {
                var metadata = await ItemMetadata.GetFromCachedIndex(root, index);
                if (metadata.ImcEntries.Count == 0 || !Imc.UsesImc(root))
                {
                    materialVariants.Add(1);
                }
                else
                {
                    foreach (var entry in metadata.ImcEntries)
                    {
                        materialVariants.Add(entry.MaterialSet);
                    }
                }
            }
            else
            {

                // Otherwise, we have to resolve all possible variants.
                var imcPath = ItemType.GetIMCPathFromChildPath(mdlPath);
                if (imcPath == null)
                {
                    // No IMC file means this Mdl doesn't use variants/only has a single variant.
                    materialVariants.Add(1);
                }
                else
                {

                    // We need to get the IMC info for this MDL so that we can pull every possible Material Variant.
                    try
                    {
                        var info = await _imc.GetFullImcInfo(imcPath, index, modlist);
                        var slotRegex = new Regex("_([a-z]{3}).mdl$");
                        var slot = "";
                        var m = slotRegex.Match(mdlPath);
                        if (m.Success)
                        {
                            slot = m.Groups[1].Value;
                        }

                        // We have to get all of the material variants used for this item now.
                        var imcInfos = info.GetAllEntries(slot, true);
                        foreach (var i in imcInfos)
                        {
                            if (i.MaterialSet != 0)
                            {
                                materialVariants.Add(i.MaterialSet);
                            }
                        }
                    }
                    catch
                    {
                        // Some Dual Wield weapons don't have any IMC entry at all.
                        // In these cases they just use Material Variant 1 (Which is usually a simple dummy material)
                        materialVariants.Add(1);
                    }
                }
            }

            // We have to get every material file that this MDL references.
            // That means every variant of every material referenced.
            var uniqueMaterialPaths = new HashSet<string>();
            foreach (var mVariant in materialVariants)
            {
                foreach (var mName in materialNames)
                {
                    // Material ID 0 is SE's way of saying it doesn't exist.
                    if (mVariant != 0)
                    {
                        var path = _mtrl.GetMtrlPath(mdlPath, mName, mVariant);
                        uniqueMaterialPaths.Add(path);
                    }
                }
            }

            if(!includeSkin)
            {
                var skinRegex = new Regex("chara/human/c[0-9]{4}/obj/body/b[0-9]{4}/material/v[0-9]{4}/.+\\.mtrl");
                var toRemove = new List<string>();
                foreach(var mtrl in uniqueMaterialPaths)
                {
                    if(skinRegex.IsMatch(mtrl))
                    {
                        toRemove.Add(mtrl);
                    }
                }

                foreach(var mtrl in toRemove)
                {
                    uniqueMaterialPaths.Remove(mtrl);
                }
            }

            return uniqueMaterialPaths.ToList();
        }



        /// <summary>
        /// Extracts just the MTRL names from a mdl file.
        /// </summary>
        /// <param name="mdlPath"></param>
        /// <param name="getOriginal"></param>
        /// <returns></returns>
        public async Task<List<string>> GetReferencedMaterialNames(string mdlPath, bool getOriginal = false, IndexFile index = null, ModList modlist = null)
        {
            var materials = new List<string>();
            var dat = new Dat(_gameDirectory);
            var modding = new Modding(_gameDirectory);


            if (index == null)
            {
                var _index = new Index(_gameDirectory);
                index = await _index.GetIndexFile(IOUtil.GetDataFileFromPath(mdlPath), false, true);
                
            }

            var offset = index.Get8xDataOffset(mdlPath);
            if (getOriginal)
            {
                if(modlist == null)
                {
                    modlist = await modding.GetModListAsync();
                }

                var mod = modlist.Mods.FirstOrDefault(x => x.fullPath == mdlPath);
                if(mod != null)
                {
                    offset = mod.data.originalOffset;
                }
            }

            if (offset == 0)
            {
                throw new Exception($"Could not find offset for {mdlPath}");
            }

            var mdlData = await dat.GetType3Data(offset, _dataFile);


            using (var br = new BinaryReader(new MemoryStream(mdlData.Data)))
            {
                // We skip the Vertex Data Structures for now
                // This is done so that we can get the correct number of meshes per LoD first
                br.BaseStream.Seek(64 + 136 * mdlData.MeshCount + 4, SeekOrigin.Begin);

                var PathCount = br.ReadInt32();
                var PathBlockSize = br.ReadInt32();


                Regex materialRegex = new Regex(".*\\.mtrl$");

                for (var i = 0; i < PathCount; i++)
                {
                    byte a;
                    List<byte> bytes = new List<byte>(); ;
                    while ((a = br.ReadByte()) != 0)
                    {
                        bytes.Add(a);
                    }

                    var st = Encoding.ASCII.GetString(bytes.ToArray()).Replace("\0", "");

                    if (materialRegex.IsMatch(st))
                    {
                        materials.Add(st);
                    }
                }

            }
            return materials;
        }


        /// <summary>
        /// Retreieves the available list of file extensions the framework has importers available for.
        /// </summary>
        /// <returns></returns>
        public List<string> GetAvailableImporters()
        {
            var cwd = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            cwd = cwd.Replace("\\", "/");
            string importerPath = cwd + "/converters/";
            var ret = new List<string>();
            ret.Add("db");  // Raw already-parsed DB files are fine.

            var directories = Directory.GetDirectories(importerPath);
            foreach (var d in directories)
            {
                var suffix = (d.Replace(importerPath, "")).ToLower();
                if (ret.IndexOf(suffix) < 0)
                {
                    ret.Add(suffix);
                }
            }
            return ret;
        }

        /// <summary>
        /// Retreieves the available list of file extensions the framework has exporters available for.
        /// </summary>
        /// <returns></returns>
        public List<string> GetAvailableExporters()
        {
            var cwd = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            cwd = cwd.Replace("\\", "/");
            string importerPath = cwd + "/converters/";
            var ret = new List<string>();
            ret.Add("obj"); // OBJ handler is internal.
            ret.Add("db");  // Raw already-parsed DB files are fine.

            var directories = Directory.GetDirectories(importerPath);
            foreach (var d in directories)
            {
                var suffix = (d.Replace(importerPath, "")).ToLower();
                if (ret.IndexOf(suffix) < 0)
                {
                    ret.Add(suffix);
                }
            }
            return ret;
        }

        // Just a default no-op function if we don't care about warning messages.
        private void NoOp(bool isWarning, string message)
        {
            //No-Op.
        }


        private async Task<string> RunExternalImporter(string importerName, string filePath, Action<bool, string> loggingFunction = null)
        {

            var cwd = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            string importerFolder = cwd + "\\converters\\" + importerName;
            if (loggingFunction == null)
            {
                loggingFunction = NoOp;
            }

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = importerFolder + "\\converter.exe",
                    Arguments = "\"" + filePath + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WorkingDirectory = "" + importerFolder + "",
                    CreateNoWindow = true
                }
            };

            // Pipe the process output to our logging function.
            proc.OutputDataReceived += (object sender, DataReceivedEventArgs e) =>
            {
                loggingFunction(false, e.Data);
            };

            // Pipe the process output to our logging function.
            proc.ErrorDataReceived += (object sender, DataReceivedEventArgs e) =>
            {
                loggingFunction(true, e.Data);
            };

            proc.EnableRaisingEvents = true;

            loggingFunction(false, "Starting " + importerName.ToUpper() + " Importer...");
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            int? code = null;
            proc.Exited += (object sender, EventArgs e) =>
            {
                code = proc.ExitCode;
            };

            return await Task.Run(async () =>
            {
                while (code == null)
                {
                    Thread.Sleep(100);
                }

                if (code != 0)
                {
                    throw (new Exception("Importer exited with error code: " + proc.ExitCode.ToString()));
                }
                return importerFolder + "\\result.db";
            });
        }

        /// <summary>
        /// Import a given model
        /// </summary>
        /// <param name="item">The current item being imported</param>
        /// <param name="race">The racial model to replace of the item</param>
        /// <param name="path">The location of the file to import</param>
        /// <param name="source">The source/application that is writing to the dat.</param>
        /// <param name="intermediaryFunction">Function to call after populating the TTModel but before converting it to a Mdl.
        ///     Takes in the new TTModel we're importing, and the old model we're overwriting.
        ///     Should return a boolean indicating whether the process should continue or cancel (false to cancel)
        /// </param>
        /// <param name="loggingFunction">
        /// Function to call when the importer receives a new log line.
        /// Takes in [bool isWarning, string message].
        /// </param>
        /// <param name="rawDataOnly">If this function should not actually finish the import and only return the raw byte data.</param>
        /// <returns>A dictionary containing any warnings encountered during import.</returns>
        public async Task ImportModel(IItemModel item, XivRace race, string path, ModelModifierOptions options = null, Action<bool, string> loggingFunction = null, Func<TTModel, TTModel, Task<bool>> intermediaryFunction = null, string source = "Unknown", string submeshId = null, bool rawDataOnly = false)
        {

            #region Setup and Validation
            if (options == null)
            {
                options = new ModelModifierOptions();
            }

            if (loggingFunction == null)
            {
                loggingFunction = NoOp;
            }

            // Test the Path.
            if (path != null && path != "")
            {
                DirectoryInfo fileLocation = null;
                try
                {
                    fileLocation = new DirectoryInfo(path);
                }
                catch (Exception ex)
                {
                    throw new IOException("Invalid file path.");
                }

                if (!File.Exists(fileLocation.FullName))
                {
                    throw new IOException("The file provided for import does not exist");
                }
            }

            var modding = new Modding(_gameDirectory);
            var dat = new Dat(_gameDirectory);

            // Resolve the current (possibly modded) Mdl.
            XivMdl currentMdl = null;
            try
            {
                currentMdl = await this.GetRawMdlData(item, race, submeshId);
            }
            catch (Exception ex)
            {
                // If we failed to load the MDL, see if we can get the unmodded MDL.
                var mdlPath = await GetMdlPath(item, race);
                var mod = await modding.TryGetModEntry(mdlPath);
                if (mod != null)
                {
                    loggingFunction(true, "Unable to load current MDL file.  Attempting to use original MDL file...");
                    currentMdl = await this.GetRawMdlData(item, race, submeshId, true);
                }
                else
                {
                    throw new Exception("Unable to load base MDL file.");
                }
            }
            #endregion

            // Wrapping this in an await ensures we're run asynchronously on a new thread.
            await Task.Run(async () =>
            {
                var filePath = currentMdl.MdlPath;

                #region TTModel Loading
                // Probably could stand to push this out to its own function later.
                var mdlPath = currentMdl.MdlPath;

                loggingFunction = loggingFunction == null ? NoOp : loggingFunction;
                loggingFunction(false, "Starting Import of file: " + path);

                var suffix = path == null || path == "" ? null : Path.GetExtension(path).ToLower().Substring(1);
                TTModel ttModel = null;


                // Loading and Running the actual Importers.
                if (path == null || path == "")
                {
                    // If we were given no path, load the current model.
                    ttModel = await GetModel(item, race, submeshId);
                }
                else if (suffix == "db")
                {
                    loggingFunction(false, "Loading intermediate file...");
                    // Raw already converted DB file, just load it.
                    ttModel = TTModel.LoadFromFile(path, loggingFunction);
                }
                else
                {
                    var dbFile = await RunExternalImporter(suffix, path, loggingFunction);
                    loggingFunction(false, "Loading intermediate file...");
                    ttModel = TTModel.LoadFromFile(dbFile, loggingFunction);
                }
                #endregion


                var sane = TTModel.SanityCheck(ttModel, loggingFunction);
                if (!sane)
                {
                    throw new InvalidDataException("Model was deemed invalid.");
                }



                // At this point we now have a fully populated TTModel entry.
                // Time to pull in the Model Modifier for any extra steps before we pass
                // it to the raw MDL creation function.


                XivMdl ogMdl = null;

                // Load the original model if we're actually going to need it.
                var mod = await modding.TryGetModEntry(mdlPath);
                if (mod != null)
                {
                    loggingFunction(false, "Loading original SE model...");
                    var ogOffset = mod.data.originalOffset;
                    ogMdl = await GetRawMdlData(item, IOUtil.GetRaceFromPath(mdlPath), submeshId, true);
                } else
                {
                    ogMdl = currentMdl;
                }

                loggingFunction(false, "Merging in existing Attribute & Material Data...");

                // Apply our Model Modifier options to the model.
                options.Apply(ttModel, currentMdl, ogMdl, loggingFunction);


                // Call the user function, if one was provided.
                if (intermediaryFunction != null)
                {
                    loggingFunction(false, "Waiting on user...");

                    // Bool says whether or not we should continue.
                    var oldModel = TTModel.FromRaw(ogMdl);
                    bool cont = await intermediaryFunction(ttModel, oldModel);
                    if (!cont)
                    {
                        loggingFunction(false, "User cancelled import process.");
                        // This feels really dumb to cancel this via a throw, but we have no other method to do so...?
                        throw new Exception("cancel");
                    }
                }

                // Fix up the skin references, just because we can/it helps user expectation.
                // Doesn't really matter as these get auto-resolved in game no matter what race they point to.
                ModelModifiers.FixUpSkinReferences(ttModel, filePath, loggingFunction);

                // Check for common user errors.
                TTModel.CheckCommonUserErrors(ttModel, loggingFunction);

                // Time to create the raw MDL.
                loggingFunction(false, "Creating MDL file from processed data...");
                var bytes = await MakeNewMdlFile(ttModel, currentMdl, loggingFunction);
                if (rawDataOnly)
                {
                    _rawData = bytes;
                    return;
                }

                var modEntry = await modding.TryGetModEntry(mdlPath);



                if (!rawDataOnly)
                {
                    loggingFunction(false, "Writing MDL File to FFXIV File System...");
                    await dat.WriteModFile(bytes, filePath, source, item);
                }

                loggingFunction(false, "Job done!");
                return;
            });
        }


        /// <summary>
        /// Creates a new Mdl file from the given data
        /// </summary>
        /// <param name="ttModel">The ttModel to import</param>
        /// <param name="ogMdl">The currently modified Mdl file.</param>
        internal async Task<byte[]> MakeNewMdlFile(TTModel ttModel, XivMdl ogMdl, Action<bool, string> loggingFunction = null)
        {
            var mdlVersion = ttModel.MdlVersion > 0 ? ttModel.MdlVersion : ogMdl.MdlVersion;
            if (loggingFunction == null)
            {
                loggingFunction = NoOp;
            }

            try
            {
                var isAlreadyModified = false;
                var isAlreadyModified2 = false;
                var totalMeshCount = 0;

                // Vertex Info
                #region Vertex Info Block

                var vertexInfoBlock = new List<byte>();
                var vertexInfoDict = new Dictionary<int, Dictionary<VertexUsageType, VertexDataType>>();

                var lodNum = 0;
                foreach (var lod in ogMdl.LoDList)
                {
                    // Higher LoDs are skipped.
                    if (lodNum > 0) continue;

                    var vdsDictionary = new Dictionary<VertexUsageType, VertexDataType>();
                    var meshMax = lodNum > 0 ? ogMdl.LoDList[lodNum].MeshCount : ttModel.MeshGroups.Count;

                    for (int meshNum = 0; meshNum < meshMax; meshNum++)
                    {
                        // Test if we have both old and new data or not.
                        var ogGroup = lod.MeshDataList.Count > meshNum ? lod.MeshDataList[meshNum] : null;
                        var ttMeshGroup = ttModel.MeshGroups.Count > meshNum ? ttModel.MeshGroups[meshNum] : null;


                        List<VertexDataStruct> source;
                        if (ogGroup == null)
                        {
                            // New Group, copy data over.
                            source = lod.MeshDataList[0].VertexDataStructList;
                        }
                        else
                        {
                            source = ogGroup.VertexDataStructList;
                        }

                        var dataSize = 0;
                        foreach (var vds in source)
                        {

                            // Padding
                            vertexInfoBlock.AddRange(new byte[4]);


                            var dataBlock = vds.DataBlock;
                            var dataOffset = vds.DataOffset;
                            var dataType = vds.DataType;
                            var dataUsage = vds.DataUsage;

                            if (lodNum == 0)
                            {

                                // Change Positions to Float from its default of Half for greater accuracy
                                // This increases the data from 8 bytes to 12 bytes
                                if (dataUsage == VertexUsageType.Position)
                                {
                                    // If the data type is already Float3 (in the case of an already modified model)
                                    // we skip it.
                                    if (dataType != VertexDataType.Float3)
                                    {
                                        dataType = VertexDataType.Float3;
                                    }
                                    else
                                    {
                                        isAlreadyModified2 = true;
                                    }
                                }

                                if(dataUsage == VertexUsageType.BoneWeight)
                                {
                                    if(mdlVersion >= 6)
                                    {
                                        dataType = VertexDataType.Ubyte4;
                                    } else
                                    {
                                        dataType = VertexDataType.Ubyte4n;
                                    }
                                }

                                // Change Normals to Float from its default of Half for greater accuracy
                                // This increases the data from 8 bytes to 12 bytes
                                if (dataUsage == VertexUsageType.Normal)
                                {
                                    // If the data type is already Float3 (in the case of an already modified model)
                                    // we skip it.
                                    if (dataType != VertexDataType.Float3)
                                    {
                                        dataType = VertexDataType.Float3;
                                    }
                                    else
                                    {
                                        isAlreadyModified = true;
                                    }
                                }

                                // Change Texture Coordinates to Float from its default of Half for greater accuracy
                                // This increases the data from 8 bytes to 16 bytes, or from 4 bytes to 8 bytes if it is a housing item
                                if (dataUsage == VertexUsageType.TextureCoordinate)
                                {
                                    if (dataType == VertexDataType.Half2 || dataType == VertexDataType.Half4)
                                    {
                                        if (dataType == VertexDataType.Half2)
                                        {
                                            dataType = VertexDataType.Float2;
                                        }
                                        else
                                        {
                                            dataType = VertexDataType.Float4;
                                        }
                                    }
                                    else
                                    {
                                        isAlreadyModified = true;
                                    }
                                }

                                // We have to adjust each offset after the Normal value because its size changed
                                // Normal is always in data block 1 and the first so its offset is 0
                                // Note: Texture Coordinates are always last so there is no need to adjust for it
                                if (dataBlock == 1 && dataOffset > 0 && !isAlreadyModified)
                                {
                                    dataOffset += 4;
                                }
                                // We have to adjust each offset after the Normal value because its size changed
                                // Normal is always in data block 1 and the first so its offset is 0
                                // Note: Texture Coordinates are always last so there is no need to adjust for it
                                if (dataBlock == 0 && dataOffset > 0 && !isAlreadyModified2)
                                {
                                    dataOffset += 4;
                                }
                            }

                            vertexInfoBlock.Add((byte)dataBlock);
                            vertexInfoBlock.Add((byte)dataOffset);
                            vertexInfoBlock.Add((byte)dataType);
                            vertexInfoBlock.Add((byte)dataUsage);

                            if (!vdsDictionary.ContainsKey(dataUsage))
                            {
                                vdsDictionary.Add(dataUsage, dataType);
                            }

                            dataSize += 8;
                        }

                        // End flag
                        vertexInfoBlock.AddRange(new byte[4]);
                        vertexInfoBlock.Add(0xFF);
                        vertexInfoBlock.AddRange(new byte[3]);

                        dataSize += 8;

                        if (dataSize < 64)
                        {
                            var remaining = 64 - dataSize;

                            vertexInfoBlock.AddRange(new byte[remaining]);
                        }

                        // Padding between data
                        vertexInfoBlock.AddRange(new byte[72]);
                    }


                    vertexInfoDict.Add(lodNum, vdsDictionary);

                    lodNum++;
                }

                // The first vertex info block does not have padding so we remove it and add it at the end
                vertexInfoBlock.RemoveRange(0, 4);
                vertexInfoBlock.AddRange(new byte[4]);
                #endregion

                // All of the data blocks for the model data
                var fullModelDataBlock = new List<byte>();

                // Path Data
                #region Path Info Block

                var pathInfoBlock = new List<byte>();

                // Path Count
                pathInfoBlock.AddRange(BitConverter.GetBytes(0)); // Dummy value to rewrite later

                // Path Block Size
                pathInfoBlock.AddRange(BitConverter.GetBytes(0)); // Dummy value to rewrite later

                var pathCount = 0;

                // Attribute paths
                var attributeOffsetList = new List<int>();

                foreach (var atr in ttModel.Attributes)
                {
                    // Attribute offset in path data block
                    attributeOffsetList.Add(pathInfoBlock.Count - 8);

                    // Path converted to bytes
                    pathInfoBlock.AddRange(Encoding.UTF8.GetBytes(atr));

                    // Byte between paths
                    pathInfoBlock.Add(0);
                    pathCount++;
                }

                // Bone paths
                var boneOffsetList = new List<int>();
                var boneStrings = new List<string>();

                // Write the full model level list of bones.
                foreach (var bone in ttModel.Bones)
                {
                    // Bone offset in path data block
                    boneOffsetList.Add(pathInfoBlock.Count - 8);

                    // Path converted to bytes
                    pathInfoBlock.AddRange(Encoding.UTF8.GetBytes(bone));
                    boneStrings.Add(bone);

                    // Byte between paths
                    pathInfoBlock.Add(0);
                    pathCount++;
                }


                // Material paths
                var materialOffsetList = new List<int>();

                foreach (var material in ttModel.Materials)
                {
                    // Material offset in path data block
                    materialOffsetList.Add(pathInfoBlock.Count - 8);

                    // Path converted to bytes
                    pathInfoBlock.AddRange(Encoding.UTF8.GetBytes(material));

                    // Byte between paths
                    pathInfoBlock.Add(0);
                    pathCount++;
                }

                // Shape paths
                var shapeOffsetList = new List<int>();

                if (ttModel.HasShapeData)
                {
                    foreach (var shape in ttModel.ShapeNames)
                    {
                        // Shape offset in path data block
                        shapeOffsetList.Add(pathInfoBlock.Count - 8);

                        // Path converted to bytes
                        pathInfoBlock.AddRange(Encoding.UTF8.GetBytes(shape));

                        // Byte between paths
                        pathInfoBlock.Add(0);
                        pathCount++;
                    }
                }

                // Extra paths
                foreach (var extra in ogMdl.PathData.ExtraPathList)
                {
                    // Path converted to bytes
                    pathInfoBlock.AddRange(Encoding.UTF8.GetBytes(extra));

                    // Byte between paths
                    pathInfoBlock.Add(0);
                    pathCount++;
                }

                // Padding before next section
                var currentSize = pathInfoBlock.Count - 8;

                // Pad out to divisions of 8 bytes.
                var pathPadding = 2;
                pathInfoBlock.AddRange(new byte[pathPadding]);

                // Go back and rewrite our counts with correct data.
                IOUtil.ReplaceBytesAt(pathInfoBlock, BitConverter.GetBytes(pathCount), 0);
                int newPathBlockSize = pathInfoBlock.Count - 8;
                IOUtil.ReplaceBytesAt(pathInfoBlock, BitConverter.GetBytes(newPathBlockSize), 4);

                // Adjust the vertex data block offset to account for the size changes;
                var oldPathBlockSize = ogMdl.PathData.PathBlockSize;
                var pathSizeDiff = newPathBlockSize - oldPathBlockSize;
                ogMdl.LoDList[0].VertexDataOffset += pathSizeDiff;


                #endregion

                // Model Data
                #region Model Data Block

                var modelDataBlock = new List<byte>();

                var ogModelData = ogMdl.ModelData;

                short meshCount = (short)(ttModel.MeshGroups.Count + ogMdl.LoDList[0].ExtraMeshCount);
                short higherLodMeshCount = 0;
                meshCount += higherLodMeshCount;
                ogModelData.MeshCount = meshCount;
                // Recalculate total number of parts.
                short partCOunt  = 0;
                foreach (var m in ttModel.MeshGroups)
                {
                    partCOunt += (short)m.Parts.Count;
                }


                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown0));
                modelDataBlock.AddRange(BitConverter.GetBytes(meshCount));
                modelDataBlock.AddRange(BitConverter.GetBytes((short)ttModel.Attributes.Count));
                modelDataBlock.AddRange(BitConverter.GetBytes(partCOunt));
                modelDataBlock.AddRange(BitConverter.GetBytes((short)ttModel.Materials.Count));
                modelDataBlock.AddRange(BitConverter.GetBytes((short)ttModel.Bones.Count));
                modelDataBlock.AddRange(BitConverter.GetBytes((short)ttModel.MeshGroups.Count));
                modelDataBlock.AddRange(BitConverter.GetBytes(ttModel.HasShapeData ? (short)ttModel.ShapeNames.Count : (short)0));
                modelDataBlock.AddRange(BitConverter.GetBytes(ttModel.HasShapeData ? (short)ttModel.ShapePartCount : (short)0));
                modelDataBlock.AddRange(BitConverter.GetBytes(ttModel.HasShapeData ? (ushort)ttModel.ShapeDataCount : (ushort)0));
                modelDataBlock.Add(1); // LoD count, set to 1 since we only use the highest LoD
                modelDataBlock.Add(ogModelData.Unknown1);
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown2));
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown3));
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown4));
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown5));
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown6)); // Unknown - Differential between gloves
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown7));
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown8)); // Unknown - Differential between gloves
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown9));
                modelDataBlock.Add(ogModelData.Unknown10a);
                modelDataBlock.Add(ogModelData.Unknown10b);
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown11));
                var boneSetSizePointer = modelDataBlock.Count;
                modelDataBlock.AddRange(BitConverter.GetBytes((short)0));
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown13));
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown14));
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown15));
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown16));
                modelDataBlock.AddRange(BitConverter.GetBytes(ogModelData.Unknown17));



                #endregion

                // Unknown Data 0
                #region Unknown Data Block 0

                var unknownDataBlock0 = ogMdl.UnkData0.Unknown;



                #endregion


                // Get the imported data
                var importDataDictionary = GetGeometryData(ttModel, vertexInfoDict);

                // Extra LoD Data
                #region Extra Level Of Detail Block
                var extraLodDataBlock = new List<byte>();

                // This seems to mostly be used in furniture, but some other things
                // use it too.  Perchberd has great info on this stuff.
                if (ogMdl.ExtraLoDList != null && ogMdl.ExtraLoDList.Count > 0)
                {
                    foreach (var lod in ogMdl.ExtraLoDList)
                    {
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.MeshOffset));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.MeshCount));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown0));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown1));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.MeshEnd));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.ExtraMeshCount));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.MeshSum));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown2));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown3));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown4));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown5));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.IndexDataStart));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown6));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown7));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.VertexDataSize));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.IndexDataSize));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.VertexDataOffset));
                        extraLodDataBlock.AddRange(BitConverter.GetBytes(lod.IndexDataOffset));
                    }


                }
                #endregion

                var rawShapeData = ttModel.GetRawShapeParts();

                // Mesh Data
                #region Mesh Data Block

                var meshDataBlock = new List<byte>();

                lodNum = 0;
                int lastVertexCount = 0;
                var previousIndexCount = 0;
                short totalParts = 0;
                var meshIndexOffsets = new List<int>();
                foreach (var lod in ogMdl.LoDList)
                {
                    if(lodNum > 0)
                    {
                        continue;
                    }

                    var meshNum = 0;
                    var previousVertexDataOffset1 = 0;
                    var previousIndexDataOffset = 0;
                    var lod0VertexDataEntrySize0 = 0;
                    var lod0VertexDataEntrySize1 = 0;

                    var lodMax = lodNum == 0 ? ttModel.MeshGroups.Count : 0;

                    for (int mi = 0; mi < lodMax; mi++)
                    {

                        bool addedMesh = meshNum >= lod.MeshCount;
                        var meshInfo = addedMesh ? null : lod.MeshDataList[meshNum].MeshInfo;

                        var vertexCount = addedMesh ? 0 : meshInfo.VertexCount;
                        var indexCount = addedMesh ? 0 : meshInfo.IndexCount;
                        var indexDataOffset = addedMesh ? 0 : meshInfo.IndexDataOffset;
                        var vertexDataOffset0 = addedMesh ? 0 : meshInfo.VertexDataOffset0;
                        var vertexDataOffset1 = addedMesh ? 0 : meshInfo.VertexDataOffset1;
                        var vertexDataOffset2 = addedMesh ? 0 : meshInfo.VertexDataOffset2;
                        byte vertexDataEntrySize0 = addedMesh ? (byte)lod0VertexDataEntrySize0 : meshInfo.VertexDataEntrySize0;
                        byte vertexDataEntrySize1 = addedMesh ? (byte)lod0VertexDataEntrySize1 : meshInfo.VertexDataEntrySize1;
                        byte vertexDataEntrySize2 = addedMesh ? (byte)0 : meshInfo.VertexDataEntrySize2;
                        short partCount = addedMesh ? (short)0 : meshInfo.MeshPartCount;
                        short materialIndex = addedMesh ? (short)0 : meshInfo.MaterialIndex;
                        short boneSetIndex = addedMesh ? (short)0 : (short)0;
                        byte vDataBlockCount = addedMesh ? (byte)0 : meshInfo.VertexDataBlockCount;

                        var ttMeshGroup = ttModel.MeshGroups[mi];
                        vertexCount = (int)ttMeshGroup.VertexCount;
                        indexCount = (int)ttMeshGroup.IndexCount;
                        partCount = (short)ttMeshGroup.Parts.Count;
                        boneSetIndex = (short)meshNum;
                        materialIndex = ttModel.GetMaterialIndex(meshNum);
                        vDataBlockCount = 2;

                        if (!addedMesh)
                        {
                            // Since we changed Normals and Texture coordinates from half to floats, we need
                            // to adjust the vertex data entry size for that data block from 24 to 36, or from 16 to 24 if its a housing item
                            // we skip this adjustment if the model is already modified
                            if (!isAlreadyModified)
                            {
                                var texCoordDataType = vertexInfoDict[0][VertexUsageType.TextureCoordinate];

                                if (texCoordDataType == VertexDataType.Float2)
                                {
                                    vertexDataEntrySize1 += 8;
                                }
                                else
                                {
                                    vertexDataEntrySize1 += 12;
                                }
                            }
                            if (!isAlreadyModified2)
                            {
                                vertexDataEntrySize0 += 4;

                            }
                        }

                        // Add in any shape vertices.
                        if (ttModel.HasShapeData)
                        {
                            // These are effectively orphaned vertices until the shape
                            // data kicks in and rewrites the triangle index list.
                            foreach (var part in ttMeshGroup.Parts)
                            {
                                foreach (var shapePart in part.ShapeParts)
                                {
                                    if (shapePart.Key.StartsWith("shp_"))
                                    {
                                        vertexCount += shapePart.Value.Vertices.Count;
                                    }
                                }
                            }
                        }

                        // Calculate new index data offset
                        if (meshNum > 0)
                        {
                            // Padding used after index data block
                            var indexPadding = 8 - previousIndexCount % 8;

                            if (indexPadding == 8)
                            {
                                indexPadding = 0;
                            }

                            indexDataOffset = previousIndexDataOffset + previousIndexCount + indexPadding;
                        }

                        // Calculate new Vertex Data Offsets
                        if (meshNum > 0)
                        {
                            vertexDataOffset0 = previousVertexDataOffset1 + lastVertexCount * vertexDataEntrySize1;

                            vertexDataOffset1 = vertexDataOffset0 + vertexCount * vertexDataEntrySize0;

                        }
                        else
                        {
                            vertexDataOffset1 = vertexCount * vertexDataEntrySize0;
                        }


                        lastVertexCount = vertexCount;

                        if (lod0VertexDataEntrySize0 == 0)
                        {
                            lod0VertexDataEntrySize0 = vertexDataEntrySize0;
                            lod0VertexDataEntrySize1 = vertexDataEntrySize1;
                        }

                        // Partless models strictly cannot have parts divisions.
                        if(ogMdl.Partless)
                        {
                            partCount = 0;
                        }

                        meshDataBlock.AddRange(BitConverter.GetBytes(vertexCount));
                        meshDataBlock.AddRange(BitConverter.GetBytes(indexCount));
                        meshDataBlock.AddRange(BitConverter.GetBytes((short)materialIndex));

                        meshDataBlock.AddRange(BitConverter.GetBytes((short)(totalParts)));
                        meshDataBlock.AddRange(BitConverter.GetBytes((short)(partCount)));
                        totalParts += partCount;

                        meshDataBlock.AddRange(BitConverter.GetBytes((short)boneSetIndex));
                        meshDataBlock.AddRange(BitConverter.GetBytes(indexDataOffset));
                        meshIndexOffsets.Add(indexDataOffset);  // Need these for later.

                        meshDataBlock.AddRange(BitConverter.GetBytes(vertexDataOffset0));
                        meshDataBlock.AddRange(BitConverter.GetBytes(vertexDataOffset1));
                        meshDataBlock.AddRange(BitConverter.GetBytes(vertexDataOffset2));
                        meshDataBlock.Add(vertexDataEntrySize0);
                        meshDataBlock.Add(vertexDataEntrySize1);
                        meshDataBlock.Add(vertexDataEntrySize2);
                        meshDataBlock.Add(vDataBlockCount);

                        previousVertexDataOffset1 = vertexDataOffset1;
                        previousIndexDataOffset = indexDataOffset;
                        previousIndexCount = indexCount;

                        meshNum++;
                    }

                    lodNum++;
                }



                #endregion

                // Unknown Attribute Data
                #region Attribute Sets

                var attrPathOffsetList = attributeOffsetList;

                var attributePathDataBlock = new List<byte>();
                foreach (var attributeOffset in attrPathOffsetList)
                {
                    attributePathDataBlock.AddRange(BitConverter.GetBytes(attributeOffset));
                }

                #endregion

                // Unknown Data 1
                #region Unknown Data Block 1

                var unknownDataBlock1 = ogMdl.UnkData1.Unknown;


                #endregion

                // Mesh Part
                #region Mesh Part Data Block

                var meshPartDataBlock = new List<byte>();

                lodNum = 0;
                if (!ogMdl.Partless)
                {

                    short currentBoneOffset = 0;
                    var previousIndexOffset = 0;
                    previousIndexCount = 0;
                    var indexOffset = 0;
                    foreach (var lod in ogMdl.LoDList)
                    {
                        var partPadding = 0;

                        // Identify the correct # of meshes
                        var meshMax = lodNum > 0 ? 0 : ttModel.MeshGroups.Count;

                        for (int meshNum = 0; meshNum < meshMax; meshNum++)
                        {
                            // Test if we have both old and new data or not.
                            var ogGroup = lod.MeshDataList.Count > meshNum ? lod.MeshDataList[meshNum] : null;
                            var ttMeshGroup = ttModel.MeshGroups.Count > meshNum ? ttModel.MeshGroups[meshNum] : null;

                            // Identify correct # of parts.
                            var partMax = lodNum == 0 ? ttMeshGroup.Parts.Count : 0;

                            // Totals for each group
                            var ogPartCount = ogGroup == null ? 0 : lod.MeshDataList[meshNum].MeshPartList.Count;
                            var newPartCount = ttMeshGroup == null ? 0 : ttMeshGroup.Parts.Count;


                            // Loop all the parts we should write.
                            for (var partNum = 0; partNum < partMax; partNum++)
                            {
                                // Get old and new data.
                                var ogPart = ogPartCount > partNum ? ogGroup.MeshPartList[partNum] : null;
                                var ttPart = newPartCount > partNum ? ttMeshGroup.Parts[partNum] : null;

                                var indexCount = 0;
                                short boneCount = 0;
                                uint attributeMask = 0;

                                if (lodNum == 0)
                                {
                                    // At LoD Zero we're not importing old FFXIV data, we're importing
                                    // the new stuff.

                                    // Recalculate Index Offset
                                    if (meshNum == 0)
                                    {
                                        if (partNum == 0)
                                        {
                                            indexOffset = 0;
                                        }
                                        else
                                        {
                                            indexOffset = previousIndexOffset + previousIndexCount;
                                        }
                                    }
                                    else
                                    {
                                        if (partNum == 0)
                                        {
                                            indexOffset = previousIndexOffset + previousIndexCount + partPadding;
                                        }
                                        else
                                        {
                                            indexOffset = previousIndexOffset + previousIndexCount;
                                        }

                                    }

                                    attributeMask = ttModel.GetAttributeBitmask(meshNum, partNum);
                                    indexCount = ttModel.MeshGroups[meshNum].Parts[partNum].TriangleIndices.Count;

                                    // Count of bones for Mesh.  High LoD Meshes get 0... Not really ideal.
                                    boneCount = (short)(lodNum == 0 ? ttMeshGroup.Bones.Count : 0);

                                    // Calculate padding between meshes
                                    if (partNum == newPartCount - 1)
                                    {
                                        var padd = (indexOffset + indexCount) % 8;

                                        if (padd != 0)
                                        {
                                            partPadding = 8 - padd;
                                        }
                                        else
                                        {
                                            partPadding = 0;
                                        }
                                    }

                                }
                                else
                                {
                                    // LoD non-zero
                                    indexCount = ogPart.IndexCount;
                                    boneCount = 0;
                                    attributeMask = 0;
                                }

                                meshPartDataBlock.AddRange(BitConverter.GetBytes(indexOffset));
                                meshPartDataBlock.AddRange(BitConverter.GetBytes(indexCount));
                                meshPartDataBlock.AddRange(BitConverter.GetBytes(attributeMask));
                                meshPartDataBlock.AddRange(BitConverter.GetBytes(currentBoneOffset));
                                meshPartDataBlock.AddRange(BitConverter.GetBytes(boneCount));

                                previousIndexCount = indexCount;
                                previousIndexOffset = indexOffset;
                                currentBoneOffset += boneCount;

                            }
                        }

                        lodNum++;
                    }
                }



                #endregion

                // Unknown Data 2
                #region Unknown Data Block 2
                var unknownDataBlock2 = ogMdl.UnkData2.Unknown;
                #endregion

                // Material Offset Data
                #region Material Data Block

                var matPathOffsetList = materialOffsetList;

                var matPathOffsetDataBlock = new List<byte>();
                foreach (var materialOffset in matPathOffsetList)
                {
                    matPathOffsetDataBlock.AddRange(BitConverter.GetBytes(materialOffset));
                }

                #endregion

                // Bone Strings Offset Data
                #region Bone Data Block

                var bonePathOffsetList = boneOffsetList;

                var bonePathOffsetDataBlock = new List<byte>();
                foreach (var boneOffset in bonePathOffsetList)
                {

                    bonePathOffsetDataBlock.AddRange(BitConverter.GetBytes(boneOffset));
                }

                #endregion

                // Bone Indices for meshes
                #region Mesh Bone Sets

                var boneSetsBlock = new List<byte>();

                if (mdlVersion >= 6)
                {
                    List<List<byte>> data = new List<List<byte>>();
                    for (var mi = 0; mi < ttModel.MeshGroups.Count; mi++)
                    {
                        data.Add(ttModel.Getv6BoneSet(mi));
                    }

                    var offset = ttModel.MeshGroups.Count;
                    for (var mi = 0; mi < ttModel.MeshGroups.Count; mi++)
                    {
                        var dataSize = data[mi].Count;
                        short count = (short) (dataSize / 2);

                        boneSetsBlock.AddRange(BitConverter.GetBytes((short) 0));
                        boneSetsBlock.AddRange(BitConverter.GetBytes((short) (count)));

                    }
                    for (var mi = 0; mi < ttModel.MeshGroups.Count; mi++)
                    {
                        var headerLocation = mi * 4;
                        var distance = (short)((boneSetsBlock.Count - headerLocation) / 4);

                        boneSetsBlock.AddRange(data[mi]);
                        if(data[mi].Count % 4 != 0)
                        {
                            boneSetsBlock.AddRange(new byte[2]);
                        }

                        // Copy in the offset information.
                        var offsetBytes = BitConverter.GetBytes(distance);
                        boneSetsBlock[headerLocation] = offsetBytes[0];
                        boneSetsBlock[headerLocation + 1] = offsetBytes[1];

                    }
                }
                else
                {
                    for (var mi = 0; mi < ttModel.MeshGroups.Count; mi++)
                    {
                        var originalBoneSet = ttModel.GetBoneSet(mi);
                        var data = originalBoneSet;
                        // Cut or pad to exactly 64 bones + blanks.  (v5 has a static array size of 128 bytes/64 shorts)
                        if (data.Count > 128)
                        {
                            data = data.GetRange(0, 128);
                        }
                        else if (data.Count < 128)
                        {
                            data.AddRange(new byte[128 - data.Count]);
                        }
                        //data.AddRange(BitConverter.GetBytes(64));//ttModel.MeshGroups[mi].Bones.Count));

                        // This is the array size... Which seems to need to be +1'd in Dawntrail for some reason.
                        if (mi == 0 || ttModel.MeshGroups[mi].Bones.Count >= 64)
                        {
                            data.AddRange(BitConverter.GetBytes(ttModel.MeshGroups[mi].Bones.Count));
                        } else
                        {
                            // DAWNTRAIL BENCHMARK HACKHACK - Add +1 to the bone count here to work around MDL v5 -> v6 in engine off-by-one error.
                            data.AddRange(BitConverter.GetBytes(ttModel.MeshGroups[mi].Bones.Count + 1));
                        }
                        
                        boneSetsBlock.AddRange(data);
                    }
                    var boneIndexListSize = boneSetsBlock.Count;
                }

                // Update the size listing.
                var sizeBytes = BitConverter.GetBytes( (short)(boneSetsBlock.Count / 2));
                modelDataBlock[boneSetSizePointer] = sizeBytes[0];
                modelDataBlock[boneSetSizePointer + 1] = sizeBytes[1];

                // Higher LoD Bone sets are omitted.

                #endregion

                #region Shape Stuff

                var FullShapeDataBlock = new List<byte>();
                if (ttModel.HasShapeData)
                {
                    #region Shape Part Counts

                    var meshShapeInfoDataBlock = new List<byte>();

                    var shapeInfoCount = ogMdl.MeshShapeData.ShapeInfoList.Count;
                    var shapePartCounts = ttModel.ShapePartCounts;

                    short runningSum = 0;
                    for (var sIdx = 0; sIdx < ttModel.ShapeNames.Count; sIdx++)
                    {

                        meshShapeInfoDataBlock.AddRange(BitConverter.GetBytes(shapeOffsetList[sIdx]));
                        var count = shapePartCounts[sIdx];

                        for (var l = 0; l < ogMdl.LoDList.Count; l++)
                        {
                            if (l == 0)
                            {
                                // LOD 0
                                meshShapeInfoDataBlock.AddRange(BitConverter.GetBytes((short)runningSum));
                                runningSum += count;

                            }
                            else
                            {
                                // LOD 1+
                                meshShapeInfoDataBlock.AddRange(BitConverter.GetBytes((short)0));
                            }
                        }
                        for (var l = 0; l < ogMdl.LoDList.Count; l++)
                        {
                            if (l == 0)
                            {
                                // LOD 0
                                meshShapeInfoDataBlock.AddRange(BitConverter.GetBytes((short)count));

                            }
                            else
                            {
                                // LOD 1+
                                meshShapeInfoDataBlock.AddRange(BitConverter.GetBytes((short)0));
                            }
                        }
                    }

                    FullShapeDataBlock.AddRange(meshShapeInfoDataBlock);

                    #endregion

                    // Mesh Shape Index Info
                    #region Shape Parts Data Block

                    var shapePartsDataBlock = new List<byte>();
                    int sum = 0;

                    foreach (var shapePart in rawShapeData)
                    {
                        shapePartsDataBlock.AddRange(BitConverter.GetBytes(meshIndexOffsets[shapePart.MeshId]));
                        shapePartsDataBlock.AddRange(BitConverter.GetBytes(shapePart.IndexReplacements.Count));
                        shapePartsDataBlock.AddRange(BitConverter.GetBytes(sum));

                        sum += shapePart.IndexReplacements.Count;
                    }

                    FullShapeDataBlock.AddRange(shapePartsDataBlock);

                    #endregion

                    // Mesh Shape Data
                    #region Raw Shape Data Data Block

                    var meshShapeDataBlock = new List<byte>();

                    var lodNumber = 0;
                    foreach (var lod in ogMdl.LoDList)
                    {
                        // We only store the shape info for LoD 0.
                        if (lodNumber == 0)
                        {
                            foreach (var p in rawShapeData)
                            {
                                var meshNum = p.MeshId;
                                foreach (var r in p.IndexReplacements)
                                {
                                    if(r.Value > ushort.MaxValue)
                                    {
                                        throw new InvalidDataException("Mesh Group " + meshNum + " has too many total vertices/triangle indices.\nRemove some vertices/faces/shapes or split them across multiple mesh groups.");
                                    }
                                    meshShapeDataBlock.AddRange(BitConverter.GetBytes((ushort)r.Key));
                                    meshShapeDataBlock.AddRange(BitConverter.GetBytes((ushort)r.Value));
                                }
                            }
                        }

                        lodNumber++;
                    }

                    FullShapeDataBlock.AddRange(meshShapeDataBlock);

                    #endregion
                }

                #endregion

                // Bone Index Part
                #region Part Bone Sets

                // These are referential arrays to subsets of their parent mesh bone set.
                // Their length is determined by the Part header's BoneCount field.
                var partBoneSetsBlock = new List<byte>();
                if (!ogMdl.Partless)
                {
                    {
                        var bones = ttModel.Bones;

                        for (var j = 0; j < ttModel.MeshGroups.Count; j++)
                        {
                            for (short i = 0; i < ttModel.MeshGroups[j].Bones.Count; i++)
                            {
                                // It's probably not perfectly performant in game, but we can just
                                // write every bone from the parent set back in here.
                                partBoneSetsBlock.AddRange(BitConverter.GetBytes(i));
                            }
                        }

                        // Higher LoDs omitted (they're given 0 bones)

                    }
                }

                partBoneSetsBlock.InsertRange(0, BitConverter.GetBytes((int)(partBoneSetsBlock.Count)));


                #endregion

                // Padding 
                #region Padding Data Block

                var paddingDataBlock = new List<byte>();

                paddingDataBlock.Add(ogMdl.PaddingSize);
                paddingDataBlock.AddRange(ogMdl.PaddedBytes);



                #endregion

                // Bounding Box
                #region Bounding Box Data Block

                var boundingBoxDataBlock = new List<byte>();

                // All right, time to tough it out and just recalculate the bounding box.
                // These are used for occlusion culling.  Primarily editing these mostly matters
                // for furniture, but helps for everything.
                float minX = 9999.0f, minY = 9999.0f, minZ = 9999.0f;
                float maxX = -9999.0f, maxY = -9999.0f, maxZ = -9999.0f;
                foreach (var m in ttModel.MeshGroups)
                {
                    foreach (var p in m.Parts)
                    {
                        foreach (var v in p.Vertices)
                        {
                            minX = minX < v.Position.X ? minX : v.Position.X;
                            minY = minY < v.Position.Y ? minY : v.Position.Y;
                            minZ = minZ < v.Position.Z ? minZ : v.Position.Z;

                            maxX = maxX > v.Position.X ? maxX : v.Position.X;
                            maxY = maxY > v.Position.Y ? maxY : v.Position.Y;
                            maxZ = maxZ > v.Position.Z ? maxZ : v.Position.Z;
                        }
                    }
                }
                var oldBb = ogMdl.BoundBox;
                var bb = new List<Vector4>(8);
                bb.Add(new Vector4(minX, minY, minZ, 1.0f));
                bb.Add(new Vector4(maxX, maxY, maxZ, 1.0f));
                bb.Add(new Vector4(minX, minY, minZ, 1.0f));
                bb.Add(new Vector4(maxX, maxY, maxZ, 1.0f));
                bb.Add(new Vector4(0.0f, 0.0f, 0.0f, 0.0f));  // Is this padding?
                bb.Add(new Vector4(0.0f, 0.0f, 0.0f, 0.0f));  // Is it some kind of magical second bounding box?
                bb.Add(new Vector4(0.0f, 0.0f, 0.0f, 0.0f));  // The world may never know...
                bb.Add(new Vector4(0.0f, 0.0f, 0.0f, 0.0f));




                foreach (var point in bb)
                {
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(point.X));
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(point.Y));
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(point.Z));
                    boundingBoxDataBlock.AddRange(BitConverter.GetBytes(point.W));
                }



                #endregion

                // Bone Transform
                #region Bone Transform Data Block

                var boneTransformDataBlock = new List<byte>();

                for (var i = 0; i < ttModel.Bones.Count; i++)
                {
                    boneTransformDataBlock.AddRange(BitConverter.GetBytes(0f));
                    boneTransformDataBlock.AddRange(BitConverter.GetBytes(0f));
                    boneTransformDataBlock.AddRange(BitConverter.GetBytes(0f));
                    boneTransformDataBlock.AddRange(BitConverter.GetBytes(0f));

                    boneTransformDataBlock.AddRange(BitConverter.GetBytes(0f));
                    boneTransformDataBlock.AddRange(BitConverter.GetBytes(0f));
                    boneTransformDataBlock.AddRange(BitConverter.GetBytes(0f));
                    boneTransformDataBlock.AddRange(BitConverter.GetBytes(0f));
                }



                #endregion

                #region LoD Block
                // Combined Data Block Sizes
                // This is the offset to the beginning of the vertex data
                var combinedDataBlockSize = 68 + vertexInfoBlock.Count + pathInfoBlock.Count + modelDataBlock.Count + unknownDataBlock0.Length + (60 * ogMdl.LoDList.Count) + extraLodDataBlock.Count + meshDataBlock.Count +
                    attributePathDataBlock.Count + (unknownDataBlock1?.Length ?? 0) + meshPartDataBlock.Count + unknownDataBlock2.Length + matPathOffsetDataBlock.Count + bonePathOffsetDataBlock.Count +
                    boneSetsBlock.Count + FullShapeDataBlock.Count + partBoneSetsBlock.Count + paddingDataBlock.Count + boundingBoxDataBlock.Count + boneTransformDataBlock.Count;

                var lodDataBlock = new List<byte>();

                lodNum = 0;
                var importVertexDataSize = 0;
                var importIndexDataSize = 0;
                var previousVertexDataSize = 0;
                var previousindexDataSize = 0;
                var previousVertexDataOffset = 0;
                short meshOffset = 0;

                foreach (var lod in ogMdl.LoDList)
                {
                    short mCount = 0;
                    // Index Data Size is recalculated for LoD 0, because of the imported data, but remains the same
                    // for every other LoD.
                    var indexDataSize = 0;

                    // Both of these index values are always the same.
                    // Because index data starts after the vertex data, these values need to be recalculated because
                    // the imported data can add/remove vertex data
                    var indexDataStart = 0;
                    var indexDataOffset = 0;


                    // This value is modified for LoD0 when import settings are present
                    // This value is recalculated for every other LoD because of the imported data can add/remove vertex data.
                    var vertexDataOffset = 0;

                    // Vertex Data Size is recalculated for LoD 0, because of the imported data, but remains the same
                    // for every other LoD.
                    var vertexDataSize = 0;

                    // Calculate the new values based on imported data
                    // Note: Only the highest quality LoD is used which is LoD 0
                    if (lodNum == 0)
                    {
                        // Get the sum of the vertex data and indices for all meshes in the imported data
                        foreach (var importData in importDataDictionary)
                        {
                            mCount++;
                            MeshData meshData;

                            bool addedMesh = false;
                            // If meshes were added, no entry exists for it in the original data, so we grab the last available mesh
                            if (importData.Key >= lod.MeshDataList.Count)
                            {
                                var diff = (importData.Key + 1) - lod.MeshDataList.Count;
                                meshData = lod.MeshDataList[importData.Key - diff];
                                addedMesh = true;
                            }
                            else
                            {
                                meshData = lod.MeshDataList[importData.Key];
                            }

                            var shapeDataCount = 0;
                            // Write the shape data if it exists.
                            if (ttModel.HasShapeData && lodNum == 0)
                            {
                                var entrySizeSum = meshData.MeshInfo.VertexDataEntrySize0 + meshData.MeshInfo.VertexDataEntrySize1;
                                if (!isAlreadyModified)
                                {
                                    var texCoordDataType = vertexInfoDict[0][VertexUsageType.TextureCoordinate];

                                    if (texCoordDataType == VertexDataType.Float2)
                                    {
                                        entrySizeSum += 8;
                                    }
                                    else
                                    {
                                        entrySizeSum += 12;
                                    }
                                }

                                var group = ttModel.MeshGroups[importData.Key];
                                var sum = 0;
                                foreach (var p in group.Parts)
                                {
                                    foreach(var shp in p.ShapeParts)
                                    {
                                        if (shp.Key.StartsWith("shp_"))
                                        {
                                            sum += shp.Value.Vertices.Count;
                                        }
                                    }
                                }
                                shapeDataCount = sum * entrySizeSum;
                            }

                            importVertexDataSize += importData.Value.VertexData0.Count + importData.Value.VertexData1.Count + shapeDataCount;

                            var indexPadding = 16 - importData.Value.IndexData.Count % 16;
                            if (indexPadding == 16)
                            {
                                indexPadding = 0;
                            }

                            importIndexDataSize += importData.Value.IndexData.Count + indexPadding;
                        }

                        vertexDataOffset = combinedDataBlockSize;

                        vertexDataSize = importVertexDataSize;
                        indexDataSize = importIndexDataSize;

                        indexDataOffset = vertexDataOffset + vertexDataSize;
                        indexDataStart = indexDataOffset;
                    }
                    else
                    {
                        // The (vertex offset + vertex data size + index data size) of the previous LoD give you the vertex offset of the current LoD
                        vertexDataOffset = previousVertexDataOffset + previousVertexDataSize + previousindexDataSize;

                        // The (vertex data offset + vertex data size) of the current LoD give you the index offset
                        // In this case it uses the newly calculated vertex data offset to get the correct index offset
                        indexDataOffset = vertexDataOffset + 0;
                        indexDataStart = indexDataOffset;
                    }

                    // We add any additional meshes to the offset if we added any through advanced importing, otherwise additionalMeshCount stays at 0
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)meshOffset));
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)mCount));

                    lodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown0));
                    lodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown1));

                    // Not sure when or how shapes are considered "extra" meshses for the sake of this var,
                    // but it seems like never?
                    short shapeMeshCount = (short)(0);
                    // We add any additional meshes to the mesh end and mesh sum if we added any through advanced imoprting, otherwise additionalMeshCount stays at 0
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)(meshOffset + mCount)));
                    lodDataBlock.AddRange(BitConverter.GetBytes(shapeMeshCount));
                    lodDataBlock.AddRange(BitConverter.GetBytes((short)(meshOffset + mCount + shapeMeshCount)));

                    // Might as well keep this updated for later.
                    totalMeshCount = meshOffset + mCount + shapeMeshCount;

                    meshOffset += (short)(mCount + shapeMeshCount);

                    lodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown2));

                    lodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown3));
                    lodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown4));
                    lodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown5));

                    lodDataBlock.AddRange(BitConverter.GetBytes(indexDataStart));

                    lodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown6));
                    lodDataBlock.AddRange(BitConverter.GetBytes(lod.Unknown7));

                    lodDataBlock.AddRange(BitConverter.GetBytes(vertexDataSize));
                    lodDataBlock.AddRange(BitConverter.GetBytes(indexDataSize));
                    lodDataBlock.AddRange(BitConverter.GetBytes(vertexDataOffset));
                    lodDataBlock.AddRange(BitConverter.GetBytes(indexDataOffset));

                    previousVertexDataSize = vertexDataSize;
                    previousindexDataSize = indexDataSize;
                    previousVertexDataOffset = vertexDataOffset;

                    lodNum++;
                }
                #endregion

                // Combine All DataBlocks
                fullModelDataBlock.AddRange(pathInfoBlock);
                fullModelDataBlock.AddRange(modelDataBlock);
                fullModelDataBlock.AddRange(unknownDataBlock0);
                fullModelDataBlock.AddRange(lodDataBlock);
                fullModelDataBlock.AddRange(extraLodDataBlock);
                fullModelDataBlock.AddRange(meshDataBlock);
                fullModelDataBlock.AddRange(attributePathDataBlock);
                if (unknownDataBlock1 != null)
                {
                    fullModelDataBlock.AddRange(unknownDataBlock1);
                }
                fullModelDataBlock.AddRange(meshPartDataBlock);
                fullModelDataBlock.AddRange(unknownDataBlock2);
                fullModelDataBlock.AddRange(matPathOffsetDataBlock);
                fullModelDataBlock.AddRange(bonePathOffsetDataBlock);
                fullModelDataBlock.AddRange(boneSetsBlock);
                fullModelDataBlock.AddRange(FullShapeDataBlock);
                fullModelDataBlock.AddRange(partBoneSetsBlock);
                fullModelDataBlock.AddRange(paddingDataBlock);
                fullModelDataBlock.AddRange(boundingBoxDataBlock);
                fullModelDataBlock.AddRange(boneTransformDataBlock);

                // Data Compression
                #region Data Compression

                var compressedMDLData = new List<byte>();

                // Vertex Info Compression
                var compressedVertexInfo = await IOUtil.Compressor(vertexInfoBlock.ToArray());
                compressedMDLData.AddRange(BitConverter.GetBytes(16));
                compressedMDLData.AddRange(BitConverter.GetBytes(0));
                compressedMDLData.AddRange(BitConverter.GetBytes(compressedVertexInfo.Length));
                compressedMDLData.AddRange(BitConverter.GetBytes(vertexInfoBlock.Count));
                compressedMDLData.AddRange(compressedVertexInfo);

                var padding = 128 - (compressedVertexInfo.Length + 16) % 128;
                compressedMDLData.AddRange(new byte[padding]);
                var compressedVertexInfoSize = compressedVertexInfo.Length + 16 + padding;

                // Model Data Compression
                var totalModelDataCompressedSize = 0;
                var compressedModelSizes = new List<int>();

                var modelDataPartCount = (int)Math.Ceiling(fullModelDataBlock.Count / 16000f);
                var modelDataPartCountsList = new List<int>(modelDataPartCount);
                var remainingDataSize = fullModelDataBlock.Count;

                for (var i = 0; i < modelDataPartCount; i++)
                {
                    if (remainingDataSize >= 16000)
                    {
                        modelDataPartCountsList.Add(16000);
                        remainingDataSize -= 16000;
                    }
                    else
                    {
                        modelDataPartCountsList.Add(remainingDataSize);
                    }
                }

                for (var i = 0; i < modelDataPartCount; i++)
                {
                    var compressedModelData =
                        await IOUtil.Compressor(fullModelDataBlock.GetRange(i * 16000, modelDataPartCountsList[i]).ToArray());

                    compressedMDLData.AddRange(BitConverter.GetBytes(16));
                    compressedMDLData.AddRange(BitConverter.GetBytes(0));
                    compressedMDLData.AddRange(BitConverter.GetBytes(compressedModelData.Length));
                    compressedMDLData.AddRange(BitConverter.GetBytes(modelDataPartCountsList[i]));
                    compressedMDLData.AddRange(compressedModelData);

                    padding = 128 - (compressedModelData.Length + 16) % 128;
                    compressedMDLData.AddRange(new byte[padding]);

                    totalModelDataCompressedSize += compressedModelData.Length + 16 + padding;
                    compressedModelSizes.Add(compressedModelData.Length + 16 + padding);
                }
                #endregion

                // Vertex Data Block
                #region Vertex Data Block

                var vertexDataSectionList = new List<VertexDataSection>();
                var compressedMeshSizes = new List<int>();
                var compressedIndexSizes = new List<int>();

                if (ttModel.HasShapeData)
                {
                    // Shape parts need to be rewitten in specific order.
                    var parts = rawShapeData;
                    foreach (var p in parts)
                    {
                        // Because our imported data does not include mesh shape data, we must include it manually
                        var group = ttModel.MeshGroups[p.MeshId];
                        var importData = importDataDictionary[p.MeshId];
                        foreach (var v in p.Vertices)
                        {
                            WriteVertex(importData, vertexInfoDict, ttModel, v);
                        }
                    }
                }


                lodNum = 0;
                foreach (var lod in ogMdl.LoDList)
                {
                    var vertexDataSection = new VertexDataSection();
                    var meshNum = 0;

                    if (lodNum == 0)
                    {
                        var totalMeshes = ttModel.MeshGroups.Count;

                        for (var i = 0; i < totalMeshes; i++)
                        {
                            var importData = importDataDictionary[meshNum];

                            vertexDataSection.VertexDataBlock.AddRange(importData.VertexData0);
                            vertexDataSection.VertexDataBlock.AddRange(importData.VertexData1);
                            vertexDataSection.IndexDataBlock.AddRange(importData.IndexData);

                            var indexPadding = (importData.IndexCount * 2) % 16;
                            if (indexPadding != 0)
                            {
                                vertexDataSection.IndexDataBlock.AddRange(new byte[16 - indexPadding]);
                            }

                            meshNum++;
                        }
                    }


                    // Vertex Compression
                    vertexDataSection.VertexDataBlockPartCount =
                        (int)Math.Ceiling(vertexDataSection.VertexDataBlock.Count / 16000f);
                    var vertexDataPartCounts = new List<int>(vertexDataSection.VertexDataBlockPartCount);
                    var remainingVertexData = vertexDataSection.VertexDataBlock.Count;

                    for (var i = 0; i < vertexDataSection.VertexDataBlockPartCount; i++)
                    {
                        if (remainingVertexData >= 16000)
                        {
                            vertexDataPartCounts.Add(16000);
                            remainingVertexData -= 16000;
                        }
                        else
                        {
                            vertexDataPartCounts.Add(remainingVertexData);
                        }
                    }

                    for (var i = 0; i < vertexDataSection.VertexDataBlockPartCount; i++)
                    {
                        var compressedVertexData = await IOUtil.Compressor(vertexDataSection.VertexDataBlock
                            .GetRange(i * 16000, vertexDataPartCounts[i]).ToArray());

                        compressedMDLData.AddRange(BitConverter.GetBytes(16));
                        compressedMDLData.AddRange(BitConverter.GetBytes(0));
                        compressedMDLData.AddRange(BitConverter.GetBytes(compressedVertexData.Length));
                        compressedMDLData.AddRange(BitConverter.GetBytes(vertexDataPartCounts[i]));
                        compressedMDLData.AddRange(compressedVertexData);

                        var vertexPadding = 128 - (compressedVertexData.Length + 16) % 128;
                        compressedMDLData.AddRange(new byte[vertexPadding]);

                        vertexDataSection.CompressedVertexDataBlockSize +=
                            compressedVertexData.Length + 16 + vertexPadding;

                        compressedMeshSizes.Add(compressedVertexData.Length + 16 + vertexPadding);
                    }

                    // Index Compression
                    vertexDataSection.IndexDataBlockPartCount =
                        (int)Math.Ceiling((vertexDataSection.IndexDataBlock.Count / 16000f));

                    var indexDataPartCounts = new List<int>(vertexDataSection.IndexDataBlockPartCount);
                    var remainingIndexData = vertexDataSection.IndexDataBlock.Count;

                    for (var i = 0; i < vertexDataSection.IndexDataBlockPartCount; i++)
                    {
                        if (remainingIndexData >= 16000)
                        {
                            indexDataPartCounts.Add(16000);
                            remainingIndexData -= 16000;
                        }
                        else
                        {
                            indexDataPartCounts.Add(remainingIndexData);
                        }
                    }

                    for (var i = 0; i < vertexDataSection.IndexDataBlockPartCount; i++)
                    {
                        var compressedIndexData = await IOUtil.Compressor(vertexDataSection.IndexDataBlock
                            .GetRange(i * 16000, indexDataPartCounts[i]).ToArray());

                        compressedMDLData.AddRange(BitConverter.GetBytes(16));
                        compressedMDLData.AddRange(BitConverter.GetBytes(0));
                        compressedMDLData.AddRange(BitConverter.GetBytes(compressedIndexData.Length));
                        compressedMDLData.AddRange(BitConverter.GetBytes(indexDataPartCounts[i]));
                        compressedMDLData.AddRange(compressedIndexData);

                        var indexPadding = 128 - (compressedIndexData.Length + 16) % 128;

                        compressedMDLData.AddRange(new byte[indexPadding]);

                        vertexDataSection.CompressedIndexDataBlockSize +=
                            compressedIndexData.Length + 16 + indexPadding;
                        compressedIndexSizes.Add(compressedIndexData.Length + 16 + indexPadding);
                    }

                    vertexDataSectionList.Add(vertexDataSection);

                    lodNum++;
                }
                #endregion

                // Header Creation
                #region Header Creation

                var datHeader = new List<byte>();

                // This is the most common size of header for models
                var headerLength = 256;

                var blockCount = compressedMeshSizes.Count + modelDataPartCount + 3 + compressedIndexSizes.Count;

                // If the data is large enough, the header length goes to the next larger size (add 128 bytes)
                if (blockCount > 24)
                {
                    var remainingBlocks = blockCount - 24;
                    var bytesUsed = remainingBlocks * 2;
                    var extensionNeeeded = (bytesUsed / 128) + 1;
                    var newSize = 256 + (extensionNeeeded * 128);
                    headerLength = newSize;
                }

                // Header Length
                datHeader.AddRange(BitConverter.GetBytes(headerLength));
                // Data Type (models are type 3 data)
                datHeader.AddRange(BitConverter.GetBytes(3));
                // Uncompressed size of the mdl file (68 is the header size (64) + vertex info block padding (4))
                var uncompressedSize = vertexInfoBlock.Count + fullModelDataBlock.Count + 68;
                // Add the vertex and index data block sizes to the uncomrpessed size
                foreach (var vertexDataSection in vertexDataSectionList)
                {
                    uncompressedSize += vertexDataSection.VertexDataBlock.Count + vertexDataSection.IndexDataBlock.Count;
                }

                datHeader.AddRange(BitConverter.GetBytes(uncompressedSize));

                // Max Buffer Size?
                datHeader.AddRange(BitConverter.GetBytes(compressedMDLData.Count / 128 + 16));
                // Buffer Size
                datHeader.AddRange(BitConverter.GetBytes(compressedMDLData.Count / 128));
                // Mdl Version
                datHeader.AddRange(BitConverter.GetBytes((short)mdlVersion));
                // Unknown
                datHeader.AddRange(BitConverter.GetBytes((short)256));

                // Vertex Info Block Uncompressed
                var datPadding = 128 - vertexInfoBlock.Count % 128;
                datPadding = datPadding == 128 ? 0 : datPadding;
                datHeader.AddRange(BitConverter.GetBytes(vertexInfoBlock.Count + datPadding));
                // Model Data Block Uncompressed
                datPadding = 128 - fullModelDataBlock.Count % 128;
                datPadding = datPadding == 128 ? 0 : datPadding;
                datHeader.AddRange(BitConverter.GetBytes(fullModelDataBlock.Count + datPadding));
                // Vertex Data Block LoD[0] Uncompressed
                datPadding = 128 - vertexDataSectionList[0].VertexDataBlock.Count % 128;
                datPadding = datPadding == 128 ? 0 : datPadding;
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[0].VertexDataBlock.Count + datPadding));
                // Vertex Data Block LoD[1] Uncompressed
                datPadding = 128 - vertexDataSectionList[1].VertexDataBlock.Count % 128;
                datPadding = datPadding == 128 ? 0 : datPadding;
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[1].VertexDataBlock.Count + datPadding));
                // Vertex Data Block LoD[2] Uncompressed
                datPadding = 128 - vertexDataSectionList[2].VertexDataBlock.Count % 128;
                datPadding = datPadding == 128 ? 0 : datPadding;
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[2].VertexDataBlock.Count + datPadding));
                // Blank 1
                datHeader.AddRange(BitConverter.GetBytes(0));
                // Blank 2
                datHeader.AddRange(BitConverter.GetBytes(0));
                // Blank 3
                datHeader.AddRange(BitConverter.GetBytes(0));
                // Index Data Block LoD[0] Uncompressed
                datPadding = 128 - vertexDataSectionList[0].IndexDataBlock.Count % 128;
                datPadding = datPadding == 128 ? 0 : datPadding;
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[0].IndexDataBlock.Count + datPadding));
                // Index Data Block LoD[1] Uncompressed
                datPadding = 128 - vertexDataSectionList[1].IndexDataBlock.Count % 128;
                datPadding = datPadding == 128 ? 0 : datPadding;
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[1].IndexDataBlock.Count + datPadding));
                // Index Data Block LoD[2] Uncompressed
                datPadding = 128 - vertexDataSectionList[2].IndexDataBlock.Count % 128;
                datPadding = datPadding == 128 ? 0 : datPadding;
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[2].IndexDataBlock.Count + datPadding));

                // Vertex Info Block Compressed
                datHeader.AddRange(BitConverter.GetBytes(compressedVertexInfoSize));
                // Model Data Block Compressed
                datHeader.AddRange(BitConverter.GetBytes(totalModelDataCompressedSize));
                // Vertex Data Block LoD[0] Compressed
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[0].CompressedVertexDataBlockSize));
                // Vertex Data Block LoD[1] Compressed
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[1].CompressedVertexDataBlockSize));
                // Vertex Data Block LoD[2] Compressed
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[2].CompressedVertexDataBlockSize));
                // Blank 1
                datHeader.AddRange(BitConverter.GetBytes(0));
                // Blank 2
                datHeader.AddRange(BitConverter.GetBytes(0));
                // Blank 3
                datHeader.AddRange(BitConverter.GetBytes(0));
                // Index Data Block LoD[0] Compressed
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[0].CompressedIndexDataBlockSize));
                // Index Data Block LoD[1] Compressed
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[1].CompressedIndexDataBlockSize));
                // Index Data Block LoD[2] Compressed
                datHeader.AddRange(BitConverter.GetBytes(vertexDataSectionList[2].CompressedIndexDataBlockSize));

                var vertexInfoOffset = 0;
                var modelDataOffset = compressedVertexInfoSize;
                var vertexDataBlock1Offset = modelDataOffset + totalModelDataCompressedSize;
                var indexDataBlock1Offset = vertexDataBlock1Offset + vertexDataSectionList[0].CompressedVertexDataBlockSize;
                var vertexDataBlock2Offset = indexDataBlock1Offset + vertexDataSectionList[0].CompressedIndexDataBlockSize;
                var indexDataBlock2Offset = vertexDataBlock2Offset + vertexDataSectionList[1].CompressedVertexDataBlockSize;
                var vertexDataBlock3Offset = indexDataBlock2Offset + vertexDataSectionList[1].CompressedIndexDataBlockSize;
                var indexDataBlock3Offset = vertexDataBlock3Offset + vertexDataSectionList[2].CompressedVertexDataBlockSize;

                // Vertex Info Offset
                datHeader.AddRange(BitConverter.GetBytes(vertexInfoOffset));
                // Model Data Offset
                datHeader.AddRange(BitConverter.GetBytes(modelDataOffset));
                // Vertex Data Block LoD[0] Offset
                datHeader.AddRange(BitConverter.GetBytes(vertexDataBlock1Offset));
                // Vertex Data Block LoD[1] Offset
                datHeader.AddRange(BitConverter.GetBytes(vertexDataBlock2Offset));
                // Vertex Data Block LoD[2] Offset
                datHeader.AddRange(BitConverter.GetBytes(vertexDataBlock3Offset));
                // Blank 1
                datHeader.AddRange(BitConverter.GetBytes(0));
                // Blank 2
                datHeader.AddRange(BitConverter.GetBytes(0));
                // Blank 3
                datHeader.AddRange(BitConverter.GetBytes(0));
                // Index Data Block LoD[0] Offset
                datHeader.AddRange(BitConverter.GetBytes(indexDataBlock1Offset));
                // Index Data Block LoD[1] Offset
                datHeader.AddRange(BitConverter.GetBytes(indexDataBlock2Offset));
                // Index Data Block LoD[2] Offset
                datHeader.AddRange(BitConverter.GetBytes(indexDataBlock3Offset));

                var vertexDataBlock1 = 1 + modelDataPartCount;
                var indexDataBlock1 = vertexDataBlock1 + vertexDataSectionList[0].VertexDataBlockPartCount;
                var vertexDataBlock2 = indexDataBlock1 + vertexDataSectionList[0].IndexDataBlockPartCount;
                var indexDataBlock2 = vertexDataBlock2 + vertexDataSectionList[1].VertexDataBlockPartCount;
                var vertexDataBlock3 = indexDataBlock2 + 1;
                var indexDataBlock3 = vertexDataBlock3 + vertexDataSectionList[2].VertexDataBlockPartCount;

                // Vertex Info Index
                datHeader.AddRange(BitConverter.GetBytes((short)0));
                // Model Data Index
                datHeader.AddRange(BitConverter.GetBytes((short)1));
                // Vertex Data Block LoD[0] Index
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataBlock1));
                // Vertex Data Block LoD[1] Index
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataBlock2));
                // Vertex Data Block LoD[2] Index
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataBlock3));
                // Blank 1 (Copies Indices?)
                datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock1));
                // Blank 2 (Copies Indices?)
                datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock2));
                // Blank 3 (Copies Indices?)
                datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock3));
                // Index Data Block LoD[0] Index
                datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock1));
                // Index Data Block LoD[1] Index
                datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock2));
                // Index Data Block LoD[2] Index
                datHeader.AddRange(BitConverter.GetBytes((ushort)indexDataBlock3));

                // Vertex Info Part Count
                datHeader.AddRange(BitConverter.GetBytes((short)1));
                // Model Data Part Count
                datHeader.AddRange(BitConverter.GetBytes((ushort)modelDataPartCount));
                // Vertex Data Block LoD[0] part count
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataSectionList[0].VertexDataBlockPartCount));
                // Vertex Data Block LoD[1] part count
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataSectionList[1].VertexDataBlockPartCount));
                // Vertex Data Block LoD[2] part count
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataSectionList[2].VertexDataBlockPartCount));
                // Blank 1
                datHeader.AddRange(BitConverter.GetBytes((short)0));
                // Blank 2
                datHeader.AddRange(BitConverter.GetBytes((short)0));
                // Blank 3
                datHeader.AddRange(BitConverter.GetBytes((short)0));
                // Index Data Block LoD[0] part count
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataSectionList[0].IndexDataBlockPartCount));
                // Index Data Block LoD[1] part count
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataSectionList[1].IndexDataBlockPartCount));
                // Index Data Block LoD[2] part count
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataSectionList[2].IndexDataBlockPartCount));

                // Mesh Count
                datHeader.AddRange(BitConverter.GetBytes((ushort)totalMeshCount));
                // Material Count
                datHeader.AddRange(BitConverter.GetBytes((ushort)ttModel.Materials.Count));
                // LoD Count
                datHeader.Add(1); // We only use the highest LoD instead of three
                // Unknown 1
                datHeader.Add(1);
                // Unknown 2
                datHeader.AddRange(BitConverter.GetBytes((short)0));

                var vertexDataBlockCount = 0;
                // Vertex Info Padded Size
                datHeader.AddRange(BitConverter.GetBytes((ushort)compressedVertexInfoSize));
                // Model Data Padded Size
                for (var i = 0; i < modelDataPartCount; i++)
                {
                    datHeader.AddRange(BitConverter.GetBytes((ushort)compressedModelSizes[i]));
                }

                // Vertex Data Block LoD[0] part padded sizes
                for (var i = 0; i < vertexDataSectionList[0].VertexDataBlockPartCount; i++)
                {
                    datHeader.AddRange(BitConverter.GetBytes((ushort)compressedMeshSizes[i]));
                }

                vertexDataBlockCount += vertexDataSectionList[0].VertexDataBlockPartCount;

                // Index Data Block LoD[0] padded size
                for (var i = 0; i < vertexDataSectionList[0].IndexDataBlockPartCount; i++)
                {
                    datHeader.AddRange(BitConverter.GetBytes((ushort)compressedIndexSizes[i]));
                }

                // Vertex Data Block LoD[1] part padded sizes
                for (var i = 0; i < vertexDataSectionList[1].VertexDataBlockPartCount; i++)
                {
                    datHeader.AddRange(BitConverter.GetBytes((ushort)compressedMeshSizes[vertexDataBlockCount + i]));
                }

                vertexDataBlockCount += vertexDataSectionList[1].VertexDataBlockPartCount;

                // Index Data Block LoD[1] padded size
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataSectionList[1].CompressedIndexDataBlockSize));

                // Vertex Data Block LoD[2] part padded sizes
                for (var i = 0; i < vertexDataSectionList[2].VertexDataBlockPartCount; i++)
                {
                    datHeader.AddRange(BitConverter.GetBytes((ushort)compressedMeshSizes[vertexDataBlockCount + i]));
                }

                // Index Data Block LoD[2] padded size
                datHeader.AddRange(BitConverter.GetBytes((ushort)vertexDataSectionList[2].CompressedIndexDataBlockSize));

                if (datHeader.Count != headerLength)
                {
                    var headerEnd = headerLength - datHeader.Count % headerLength;
                    datHeader.AddRange(new byte[headerEnd]);
                }

                // Add the header to the MDL data
                compressedMDLData.InsertRange(0, datHeader);

                return compressedMDLData.ToArray();

                #endregion
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        /// <summary>
        /// Converts the TTTModel Geometry into the raw byte blocks FFXIV expects.
        /// </summary>
        /// <param name="colladaMeshDataList">The list of mesh data obtained from the imported collada file</param>
        /// <param name="itemType">The item type</param>
        /// <returns>A dictionary containing the vertex byte data per mesh</returns>
        private Dictionary<int, VertexByteData> GetGeometryData(TTModel ttModel, Dictionary<int, Dictionary<VertexUsageType, VertexDataType>> vertexInfoDict)
        {
            var importDataDictionary = new Dictionary<int, VertexByteData>();

            var meshNumber = 0;


            // Add the first vertex data set to the ImportData list
            // This contains [ Position, Bone Weights, Bone Indices]
            foreach (var m in ttModel.MeshGroups)
            {
                var importData = new VertexByteData()
                {
                    VertexData0 = new List<byte>(),
                    VertexData1 = new List<byte>(),
                    IndexData = new List<byte>(),
                    VertexCount = (int)m.VertexCount,
                    IndexCount = (int)m.IndexCount
                };

                foreach (var p in m.Parts)
                {
                    foreach (var v in p.Vertices)
                    {
                        WriteVertex(importData, vertexInfoDict, ttModel, v);
                    }

                }

                var count = m.IndexCount;
                for (var i = 0; i < count; i++)
                {
                    var index = m.GetIndexAt(i);
                    importData.IndexData.AddRange(BitConverter.GetBytes(((ushort)index)));
                }
                // Add the import data to the dictionary
                importDataDictionary.Add(meshNumber, importData);
                meshNumber++;
            }

            return importDataDictionary;
        }

        /// <summary>
        /// Converts a given Vector 3 Binormal into the the byte4 format SE uses for storing Binormal data.
        /// </summary>
        /// <param name="normal"></param>
        /// <param name="handedness"></param>
        /// <returns></returns>
        private static List<byte> ConvertVectorBinormalToBytes(Vector3 normal, int handedness)
        {
            // These four byte vector values are represented as
            // [ Byte x, Byte y, Byte z, Byte handedness(0/255) ]


            // Now, this is where things get a little weird compared to storing most 3D Models.
            // SE's standard format is to include BINOMRAL(aka Bitangent) data, but leave TANGENT data out, to be calculated on the fly from the BINORMAL data.
            // This is kind of reverse compared to most math you'll find where the TANGENT is kept, and the BINORMAL is calculated on the fly. (Or both are kept/both are generated on load)

            // The Binormal data has already had the handedness applied to generate an appropriate binormal, but we store
            // that handedness after for use when the game (or textools) regenerates the Tangent from the Normal + Binormal.

            var bytes = new List<byte>(4);
            var vec = normal;
            vec.Normalize();


            // The possible range of -1 to 1 Vector X/Y/Z Values are compressed
            // into a 0-255 range.

            // A simple way to solve this cleanly is to translate the vector by [1] in all directions
            // So the vector's range is 0 to 2.
            vec += Vector3.One;

            // And then multiply the resulting value times (255 / 2), and round off the result.
            // This helps minimize errors that arise from quirks in floating point arithmetic.
            var x = (byte)Math.Round(vec.X * (255f / 2f));
            var y = (byte)Math.Round(vec.Y * (255f / 2f));
            var z = (byte)Math.Round(vec.Z * (255f / 2f));


            bytes.Add(x);
            bytes.Add(y);
            bytes.Add(z);

            // Add handedness bit
            if (handedness > 0)
            {
                bytes.Add(0);
            }
            else
            {
                bytes.Add(255);
            }

            return bytes;
        }

        /// <summary>
        /// Get the vertex data in byte format
        /// </summary>
        /// <param name="vertexData">The vertex data to convert</param>
        /// <param name="itemType">The item type</param>
        /// <returns>A class containing the byte data for the given data</returns>
        private static VertexByteData GetVertexByteData(VertexData vertexData, Dictionary<VertexUsageType, VertexDataType> vertexInfoDict, bool hasWeights)
        {
            var vertexByteData = new VertexByteData
            {
                VertexCount = vertexData.Positions.Count,
                IndexCount = vertexData.Indices.Count
            };

            // Vertex Block 0
            for (var i = 0; i < vertexData.Positions.Count; i++)
            {
                if (vertexInfoDict[VertexUsageType.Position] == VertexDataType.Half4)
                {
                    var x = new Half(vertexData.Positions[i].X);
                    var y = new Half(vertexData.Positions[i].Y);
                    var z = new Half(vertexData.Positions[i].Z);

                    vertexByteData.VertexData0.AddRange(BitConverter.GetBytes(x.RawValue));
                    vertexByteData.VertexData0.AddRange(BitConverter.GetBytes(y.RawValue));
                    vertexByteData.VertexData0.AddRange(BitConverter.GetBytes(z.RawValue));

                    // Half float positions have a W coordinate but it is never used and is defaulted to 1.
                    var w = new Half(1.0f);
                    vertexByteData.VertexData0.AddRange(BitConverter.GetBytes(w.RawValue));
                }
                // If positions are not Half values, they are single values
                else
                {
                    vertexByteData.VertexData0.AddRange(BitConverter.GetBytes(vertexData.Positions[i].X));
                    vertexByteData.VertexData0.AddRange(BitConverter.GetBytes(vertexData.Positions[i].Y));
                    vertexByteData.VertexData0.AddRange(BitConverter.GetBytes(vertexData.Positions[i].Z));
                }

                if (hasWeights)
                {
                    // Bone Weights
                    foreach (var boneWeight in vertexData.BoneWeights[i])
                    {
                        vertexByteData.VertexData0.Add((byte)Math.Round(boneWeight * 255f));
                    }

                    // Bone Indices
                    vertexByteData.VertexData0.AddRange(vertexData.BoneIndices[i]);
                }
            }

            // Vertex Block 1
            for (var i = 0; i < vertexData.Normals.Count; i++)
            {
                if (vertexInfoDict[VertexUsageType.Normal] == VertexDataType.Half4)
                {
                    // Normals
                    var x = new Half(vertexData.Normals[i].X);
                    var y = new Half(vertexData.Normals[i].Y);
                    var z = new Half(vertexData.Normals[i].Z);
                    var w = new Half(0);

                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(x.RawValue));
                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(y.RawValue));
                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(z.RawValue));
                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(w.RawValue));
                }
                else
                {
                    // Normals
                    var x = vertexData.Normals[i].X;
                    var y = vertexData.Normals[i].Y;
                    var z = vertexData.Normals[i].Z;

                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(x));
                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(y));
                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(z));
                }


                // BiNormals - GetVertexByteData
                vertexByteData.VertexData1.AddRange(ConvertVectorBinormalToBytes(vertexData.BiNormals[i], vertexData.BiNormalHandedness[i]));

                // Tangents
                if (vertexInfoDict.ContainsKey(VertexUsageType.Tangent))
                {
                    vertexByteData.VertexData1.AddRange(ConvertVectorBinormalToBytes(vertexData.Tangents[i], vertexData.BiNormalHandedness[i]));
                }

                // Colors
                if (vertexData.Colors.Count > 0)
                {
                    var colorVector = vertexData.Colors[i].ToVector4();

                    vertexByteData.VertexData1.Add((byte)(colorVector.W * 255));
                    vertexByteData.VertexData1.Add((byte)(colorVector.X * 255));
                    vertexByteData.VertexData1.Add((byte)(colorVector.Y * 255));
                    vertexByteData.VertexData1.Add((byte)(colorVector.Z * 255));
                }

                var texCoordDataType = vertexInfoDict[VertexUsageType.TextureCoordinate];

                if (texCoordDataType == VertexDataType.Float2 || texCoordDataType == VertexDataType.Float4)
                {
                    var tc0x = vertexData.TextureCoordinates0[i].X;
                    var tc0y = vertexData.TextureCoordinates0[i].Y * -1f;

                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(tc0x));
                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(tc0y));

                    if (vertexData.TextureCoordinates1.Count > 0)
                    {
                        var tc1x = vertexData.TextureCoordinates1[i].X;
                        var tc1y = vertexData.TextureCoordinates1[i].Y;

                        vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(tc1x));
                        vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(tc1y));
                    }
                }
                else
                {
                    // Texture Coordinates
                    var tc0x = new Half(vertexData.TextureCoordinates0[i].X);
                    var tc0y = new Half(vertexData.TextureCoordinates0[i].Y);

                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(tc0x.RawValue));
                    vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(tc0y.RawValue));

                    if (vertexData.TextureCoordinates1.Count > 0)
                    {
                        var tc1x = new Half(vertexData.TextureCoordinates1[i].X);
                        var tc1y = new Half(vertexData.TextureCoordinates1[i].Y);

                        vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(tc1x.RawValue));
                        vertexByteData.VertexData1.AddRange(BitConverter.GetBytes(tc1y.RawValue));
                    }
                }

            }

            // Indices
            foreach (var index in vertexData.Indices)
            {
                vertexByteData.IndexData.AddRange(BitConverter.GetBytes((ushort)index));
            }

            return vertexByteData;
        }

        private void WriteVertex(VertexByteData importData, Dictionary<int, Dictionary<VertexUsageType, VertexDataType>> vertexInfoDict, TTModel model, TTVertex v)
        {
            // Positions for Weapon and Monster item types are half precision floating points
            var posDataType = vertexInfoDict[0][VertexUsageType.Position];


            Half hx, hy, hz;
            if (posDataType == VertexDataType.Half4)
            {
                hx = new Half(v.Position[0]);
                hy = new Half(v.Position[1]);
                hz = new Half(v.Position[2]);

                importData.VertexData0.AddRange(BitConverter.GetBytes(hx.RawValue));
                importData.VertexData0.AddRange(BitConverter.GetBytes(hy.RawValue));
                importData.VertexData0.AddRange(BitConverter.GetBytes(hz.RawValue));

                // Half float positions have a W coordinate but it is never used and is defaulted to 1.
                var w = new Half(1);
                importData.VertexData0.AddRange(BitConverter.GetBytes(w.RawValue));

            }
            // Everything else has positions as singles 
            else
            {
                importData.VertexData0.AddRange(BitConverter.GetBytes(v.Position[0]));
                importData.VertexData0.AddRange(BitConverter.GetBytes(v.Position[1]));
                importData.VertexData0.AddRange(BitConverter.GetBytes(v.Position[2]));
            }

            // Furniture items do not have bone data
            if (model.HasWeights)
            {
                // Bone Weights
                importData.VertexData0.AddRange(v.Weights);

                // Bone Indices
                importData.VertexData0.AddRange(v.BoneIds);
            }

            // Normals
            hx = v.Normal[0];
            hy = v.Normal[1];
            hz = v.Normal[2];

            importData.VertexData1.AddRange(BitConverter.GetBytes(hx));
            importData.VertexData1.AddRange(BitConverter.GetBytes(hy));
            importData.VertexData1.AddRange(BitConverter.GetBytes(hz));

            // BiNormals
            // Change the BiNormals based on Handedness
            var biNormal = v.Binormal;
            int handedness = v.Handedness ? -1 : 1;

            // This part makes sense - Handedness defines when you need to flip the tangent/binormal...
            // But the data gets written into the game, too, so why do we need to pre-flip it?

            importData.VertexData1.AddRange(ConvertVectorBinormalToBytes(biNormal, handedness));


            if (vertexInfoDict[0].ContainsKey(VertexUsageType.Tangent))
            {
                // 99% sure this code path is never actually used.
                importData.VertexData1.AddRange(ConvertVectorBinormalToBytes(v.Tangent, handedness * -1));
            }


            if (vertexInfoDict[0].ContainsKey(VertexUsageType.Color))
            {
                importData.VertexData1.Add(v.VertexColor[0]);
                importData.VertexData1.Add(v.VertexColor[1]);
                importData.VertexData1.Add(v.VertexColor[2]);
                importData.VertexData1.Add(v.VertexColor[3]);
            }

            // Texture Coordinates
            var texCoordDataType = vertexInfoDict[0][VertexUsageType.TextureCoordinate];


            importData.VertexData1.AddRange(BitConverter.GetBytes(v.UV1[0]));
            importData.VertexData1.AddRange(BitConverter.GetBytes(v.UV1[1]));

            if (texCoordDataType == VertexDataType.Float4)
            {
                importData.VertexData1.AddRange(BitConverter.GetBytes(v.UV2[0]));
                importData.VertexData1.AddRange(BitConverter.GetBytes(v.UV2[1]));
            }
        }


        /// <summary>
        /// Classes used in reading bone deformation data.
        /// </summary>
        private class DeformationBoneSet
        {
            public List<DeformationBoneData> Data = new List<DeformationBoneData>();
        }
        private class DeformationBoneData
        {
            public string Name;
            public float[] Matrix = new float[16];
        }


        /// <summary>
        /// Loads the deformation files for attempting racial deformation
        /// Currently in debugging phase.
        /// </summary>
        /// <param name="race"></param>
        /// <param name="deformations"></param>
        /// <param name="recalculated"></param>
        public static void GetDeformationMatrices(XivRace race, out Dictionary<string, Matrix> deformations, out Dictionary<string, Matrix> invertedDeformations, out Dictionary<string, Matrix> normalDeformations, out Dictionary<string, Matrix> invertedNormalDeformations)
        {
            deformations = new Dictionary<string, Matrix>();
            invertedDeformations = new Dictionary<string, Matrix>();
            normalDeformations = new Dictionary<string, Matrix>();
            invertedNormalDeformations = new Dictionary<string, Matrix>();


            var deformFile = "Skeletons/c" + race.GetRaceCode() + "_deform.json";
            var deformationLines = File.ReadAllLines(deformFile);
            string deformationJson = deformationLines[0];
            var deformationData = JsonConvert.DeserializeObject<DeformationBoneSet>(deformationJson);
            foreach (var set in deformationData.Data)
            {
                deformations.Add(set.Name, new Matrix(set.Matrix));
            }

            var skelName = "c" + race.GetRaceCode();
            var cwd = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            var skeletonFile = cwd + "/Skeletons/" + skelName + "b0001.skel";

            if (!File.Exists(skeletonFile))
            {
                // Need to extract the Skel file real quick like.
                var tempRoot = new XivDependencyRootInfo();
                tempRoot.PrimaryType = XivItemType.equipment;
                tempRoot.PrimaryId = 0;
                tempRoot.Slot = "top";
                Task.Run(async () =>
                {
                    await Sklb.GetBaseSkeletonFile(tempRoot, race);
                }).Wait();
            }

            var skeletonData = File.ReadAllLines(skeletonFile);
            var FullSkel = new Dictionary<string, SkeletonData>();
            var FullSkelNum = new Dictionary<int, SkeletonData>();

            // Deserializes the json skeleton file and makes 2 dictionaries with names and numbers as keys
            foreach (var b in skeletonData)
            {
                if (b == "") continue;
                var j = JsonConvert.DeserializeObject<SkeletonData>(b);

                FullSkel.Add(j.BoneName, j);
                FullSkelNum.Add(j.BoneNumber, j);
            }

            var root = FullSkel["n_root"];

            BuildNewTransfromMatrices(root, FullSkel, deformations, invertedDeformations, normalDeformations, invertedNormalDeformations);
        }
        private static void BuildNewTransfromMatrices(SkeletonData node, Dictionary<string, SkeletonData> skeletonData, Dictionary<string, Matrix> deformations, Dictionary<string, Matrix> invertedDeformations, Dictionary<string, Matrix> normalDeformations, Dictionary<string, Matrix> invertedNormalDeformations)
        {
            if (node.BoneParent == -1)
            {
                if (!deformations.ContainsKey(node.BoneName))
                {
                    deformations.Add(node.BoneName, Matrix.Identity);
                }
                invertedDeformations.Add(node.BoneName, Matrix.Identity);
                normalDeformations.Add(node.BoneName, Matrix.Identity);
                invertedNormalDeformations.Add(node.BoneName, Matrix.Identity);
            }
            else
            {
                if (deformations.ContainsKey(node.BoneName))
                {
                    invertedDeformations.Add(node.BoneName, deformations[node.BoneName].Inverted());

                    var normalMatrix = deformations[node.BoneName].Inverted();
                    normalMatrix.Transpose();
                    normalDeformations.Add(node.BoneName, normalMatrix);

                    var invertexNormalMatrix = deformations[node.BoneName].Inverted();
                    normalMatrix.Transpose();
                    invertexNormalMatrix.Invert();
                    invertedNormalDeformations.Add(node.BoneName, invertexNormalMatrix);

                }
                else
                {
                    if (!skeletonData.ContainsKey(node.BoneName))
                    {
                        deformations[node.BoneName] = Matrix.Identity;
                        invertedDeformations[node.BoneName] = Matrix.Identity;
                        normalDeformations[node.BoneName] = Matrix.Identity;
                        invertedNormalDeformations[node.BoneName] = Matrix.Identity;
                    }
                    else
                    {
                        var skelEntry = skeletonData[node.BoneName];
                        while (skelEntry != null)
                        {
                            if (deformations.ContainsKey(skelEntry.BoneName))
                            {
                                // This parent has a deform.
                                deformations[node.BoneName] = deformations[skelEntry.BoneName];
                                invertedDeformations[node.BoneName] = invertedDeformations[skelEntry.BoneName];
                                normalDeformations[node.BoneName] = normalDeformations[skelEntry.BoneName];
                                invertedNormalDeformations[node.BoneName] = invertedNormalDeformations[skelEntry.BoneName];
                                break;
                            }

                            // Seek our next parent.
                            skelEntry = skeletonData.FirstOrDefault(x => x.Value.BoneNumber == skelEntry.BoneParent).Value;
                        }

                        if (skelEntry == null)
                        {
                            deformations[node.BoneName] = Matrix.Identity;
                            invertedDeformations[node.BoneName] = Matrix.Identity;
                            normalDeformations[node.BoneName] = Matrix.Identity;
                            invertedNormalDeformations[node.BoneName] = Matrix.Identity;
                        }
                    }
                }
            }
            var children = skeletonData.Where(x => x.Value.BoneParent == node.BoneNumber);
            foreach (var c in children)
            {
                BuildNewTransfromMatrices(c.Value, skeletonData, deformations, invertedDeformations, normalDeformations, invertedNormalDeformations);
            }
        }

        private string _EquipmentModelPathFormat = "chara/equipment/e{0}/model/c{1}e{0}_{2}.mdl";
        private string _AccessoryModelPathFormat = "chara/accessory/a{0}/model/c{1}a{0}_{2}.mdl";

        public static bool IsAutoAssignableModel(string mdlPath)
        {
            if (!mdlPath.StartsWith("chara/"))
            {
                return false;
            }

            if (!mdlPath.EndsWith(".mdl"))
            {
                return false;
            }

            // Ensure Midlander F Based model.
            if (
                (!mdlPath.Contains("c0201"))
                && (!mdlPath.Contains("c0401"))
                && (!mdlPath.Contains("c0601"))
                && (!mdlPath.Contains("c0801"))
                && (!mdlPath.Contains("c1001"))
                && (!mdlPath.Contains("c1401"))
                && (!mdlPath.Contains("c1601"))
                && (!mdlPath.Contains("c1801")))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Performs a heuristic check on the UV data of the given model to determine if its skin material assignment needs to be altered.
        /// Primarily for handling Gen3/Bibo+ compat issues on Female Model skin materials B/D.
        /// </summary>
        /// <param name="mdlPath"></param>
        /// <returns></returns>
        public async Task<bool> CheckSkinAssignment(string mdlPath, IndexFile _index, ModList _modlist)
        {
            if(!IsAutoAssignableModel(mdlPath))
            {
                return false;
            }

            var ogMdl = await GetRawMdlData(mdlPath);
            var ttMdl = TTModel.FromRaw(ogMdl);

            bool anyChanges = false;
            anyChanges = SkinCheckBibo(ttMdl, _index);

            if(!anyChanges)
            {
                anyChanges = SkinCheckAndrofirm(ttMdl);
            }

            if (!anyChanges)
            {
                anyChanges = SkinCheckGen3(ttMdl);
            }


            if (anyChanges)
            {

                var bytes = await MakeNewMdlFile(ttMdl, ogMdl);

                // We know by default that a mod entry exists for this file if we're actually doing the check process on it.
                var modEntry = _modlist.Mods.First(x => x.fullPath == mdlPath);
                var _dat = new Dat(XivCache.GameInfo.GameDirectory);
                
                await _dat.WriteModFile(bytes, mdlPath, modEntry.source, null, _index, _modlist, modEntry.modPack);

            }

            return anyChanges;
        }

        /// <summary>
        /// Loops through all mods in the modlist to update their skin assignments, performing a batch save at the end if 
        /// everything was successful.
        /// </summary>
        /// <returns></returns>
        public async Task<int> CheckAllModsSkinAssignments()
        {
            var _modding = new Modding(XivCache.GameInfo.GameDirectory);
            var modList = await _modding.GetModListAsync();

            var _index = new Index(XivCache.GameInfo.GameDirectory);
            var index = await _index.GetIndexFile(XivDataFile._04_Chara);

            int count = 0;
            foreach(var mod in modList.Mods)
            {
                var changed = await CheckSkinAssignment(mod.fullPath, index, modList);
                if(changed)
                {
                    count++;
                }
            }

            if(count > 0)
            {
                await _index.SaveIndexFile(index);
                await _modding.SaveModListAsync(modList);
            }

            return count;
        }
        private bool SkinCheckGen2()
        {
            // If something is on Mat A, we can just assume it's fine realistically to save time.

            // For now this is unneeded as Mat A things are default Gen2, and there are some derivatives of Gen2 on other materials
            // which would complicate this check.
            return false;
        }

        private bool SkinCheckGen3(TTModel ttMdl)
        {
            // Standard forward check.  Primarily this is looking for Mat D materials that are 'gen3 compat patch' for bibo.
            // To pull them back onto mat B.

            // Standard forward check.  Primarily this is looking for standard Mat B bibo materials,
            // To pull them onto mat D.

            var layout = GetUVHashSet("gen3");
            if (layout == null || layout.Count == 0) return false;

            bool anyChanges = false;
            foreach (var mg in ttMdl.MeshGroups)
            {
                var rex = ModelModifiers.SkinMaterialRegex;
                if (rex.IsMatch(mg.Material))
                {
                    var extractRex = new Regex("_([a-z]+)\\.mtrl$");
                    var res = extractRex.Match(mg.Material);
                    if (!res.Success) continue;

                    var matId = res.Groups[1].Value;
                    if (matId != "d") continue;

                    // We have a Material B skin reference in a Hyur F model.

                    var totalVerts = mg.VertexCount;

                    // Take 100 evenly divided samples, or however many we can get if there's not enough verts.
                    uint sampleCount = 100;
                    int sampleDivision = (int)(totalVerts / sampleCount);
                    if (sampleDivision <= 0)
                    {
                        sampleDivision = 1;
                    }

                    uint hits = 0;
                    const float requiredRatio = 0.5f;


                    var realSamples = 0;
                    for (int i = 0; i < totalVerts; i += sampleDivision)
                    {
                        realSamples++;
                        // Get a random vertex.
                        var vert = mg.GetVertexAt(i);

                        var fx = vert.UV1[0];
                        var fy = vert.UV1[1];
                        // Sort quadrant.
                        while (fy < -1)
                        {
                            fy += 1;
                        }
                        while (fy > 0)
                        {
                            fy -= 1;
                        }

                        while (fx > 1)
                        {
                            fx -= 1;
                        }
                        while (fx < 0)
                        {
                            fx += 1;
                        }

                        // This is a simple hash comparison checking if the 
                        // UVs are bytewise idential at half precision.
                        // In the future a better comparison method may be needed,
                        // but this is super fast as it is.
                        var huv = new HalfUV(fx, fy);
                        if (layout.Contains(huv))
                        {
                            hits++;
                        }
                    }

                    float ratio = (float)hits / (float)realSamples;


                    // This Mesh group needs to be swapped.
                    if (ratio >= requiredRatio)
                    {
                        mg.Material = mg.Material.Replace("_" + matId + ".mtrl", "_b.mtrl");
                        anyChanges = true;
                    }
                }
            }
            return anyChanges;
        }

        private bool SkinCheckBibo(TTModel ttMdl, IndexFile _index)
        {
            // Standard forward check.  Primarily this is looking for standard Mat B bibo materials,
            // To pull them onto mat D.

            const string bibo_path = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";
            bool biboPathExists = _index.FileExists(bibo_path);
            var layout = GetUVHashSet("bibo");
            if (layout == null || layout.Count == 0) return false;

            bool anyChanges = false;
            foreach (var mg in ttMdl.MeshGroups)
            {
                var rex = ModelModifiers.SkinMaterialRegex;
                if (rex.IsMatch(mg.Material))
                {
                    var extractRex = new Regex("_([a-z]+)\\.mtrl$");
                    var res = extractRex.Match(mg.Material);
                    if (!res.Success) continue;

                    var matId = res.Groups[1].Value;

                    // We only care if this is a B material model or D material model with a _bibo path to move it to.
                    if(!(matId == "b" || (matId == "d" && biboPathExists)))
                    {
                        continue;
                    }

                    // We have a Material B skin reference in a Hyur F model.

                    var totalVerts = mg.VertexCount;

                    // Take 100 evenly divided samples, or however many we can get if there's not enough verts.
                    uint sampleCount = 100;
                    int sampleDivision = (int)(totalVerts / sampleCount);
                    if (sampleDivision <= 0)
                    {
                        sampleDivision = 1;
                    }

                    uint hits = 0;
                    const float requiredRatio = 0.5f;


                    var realSamples = 0;
                    for (int i = 0; i < totalVerts; i += sampleDivision)
                    {
                        realSamples++;
                        // Get a random vertex.
                        var vert = mg.GetVertexAt(i);

                        var fx = vert.UV1[0];
                        var fy = vert.UV1[1];
                        // Sort quadrant.
                        while (fy < -1)
                        {
                            fy += 1;
                        }
                        while (fy > 0)
                        {
                            fy -= 1;
                        }

                        while (fx > 1)
                        {
                            fx -= 1;
                        }
                        while (fx < 0)
                        {
                            fx += 1;
                        }

                        // This is a simple hash comparison checking if the 
                        // UVs are bytewise idential at half precision.
                        // In the future a better comparison method may be needed,
                        // but this is super fast as it is.
                        var huv = new HalfUV(fx, fy);
                        if (layout.Contains(huv))
                        {
                            hits++;
                        }
                    }

                    float ratio = (float)hits / (float)realSamples;


                    // This Mesh group needs to be swapped.
                    if (ratio >= requiredRatio)
                    {
                        // If the new _bibo material actually exists, move it.
                        if(biboPathExists)
                        {
                            mg.Material = mg.Material.Replace("_" + matId + ".mtrl", "_bibo.mtrl");
                        } else
                        {
                            mg.Material = mg.Material.Replace("_" + matId + ".mtrl", "_d.mtrl");
                        }


                        anyChanges = true;
                    }
                }
            }

            // IF we moved files onto _bibo, we should move any pubes as well onto their pubic hair path.
            if (anyChanges && biboPathExists)
            {
                foreach (var mg in ttMdl.MeshGroups)
                {
                    var rex = ModelModifiers.SkinMaterialRegex;
                    if (rex.IsMatch(mg.Material))
                    {
                        var extractRex = new Regex("_([a-z]+)\\.mtrl$");
                        var res = extractRex.Match(mg.Material);
                        if (!res.Success) continue;

                        var matId = res.Groups[1].Value;
                        if (matId != "c") continue;

                        mg.Material = mg.Material.Replace("_" + matId + ".mtrl", "_bibopube.mtrl");
                    }
                }
            }

            return anyChanges;
        }


        private bool SkinCheckAndrofirm(TTModel ttMdl)
        {
            // AF is a bit of a special case.
            // It's a derivative of Gen2, that only varies on the legs.
            // So if it's anything other than a leg model, we can pass, since it's really just a gen2 model.

            // If it /is/ a leg model though, we have to create a hashset of the 
            // UVs in the material, then reverse check
            // So we have to sample the heuristic data, and see if there are
            // a sufficient amount of matches in the model.

            if (!ttMdl.Source.EndsWith("_dwn.mdl")) return false;

            var layout = GetUVHashSet("androfirm");
            if (layout == null || layout.Count == 0) return false;


            HashSet<HalfUV> modelUVs = new HashSet<HalfUV>();
            List<TTMeshGroup> meshes = new List<TTMeshGroup>();
            bool anyChanges = false;
            foreach (var mg in ttMdl.MeshGroups)
            {
                var rex = ModelModifiers.SkinMaterialRegex;
                if (rex.IsMatch(mg.Material))
                {
                    var extractRex = new Regex("_([a-z]+)\\.mtrl$");
                    var res = extractRex.Match(mg.Material);
                    if (!res.Success) continue;

                    var matId = res.Groups[1].Value;
                    if (matId != "a") continue; // Androfirm was originally published on the A Material.

                    var totalVerts = mg.VertexCount;

                    meshes.Add(mg);

                    for (int i = 0; i < totalVerts; i++)
                    {
                        // Get vertex
                        var vert = mg.GetVertexAt(i);

                        var fx = vert.UV1[0];
                        var fy = vert.UV1[1];

                        // Sort quadrant.
                        while (fy < -1)
                        {
                            fy += 1;
                        }
                        while (fy > 0)
                        {
                            fy -= 1;
                        }

                        while (fx > 1)
                        {
                            fx -= 1;
                        }
                        while (fx < 0)
                        {
                            fx += 1;
                        }

                        // Add to HashSet
                        modelUVs.Add(new HalfUV(fx, fy));
                    }
                }
            }

            if (modelUVs.Count == 0) return false;

            // We have some amount of material A leg UVs.

            var layoutVerts = layout.Count;
            var desiredSamples = 100;
            var skip = layoutVerts / desiredSamples;
            var hits = 0;
            var realSamples = 0;

            // Have to itterate these because can't index access a hashset.
            // maybe cache an array version later if speed proves to be an issue?
            var id = 0;
            foreach(var uv in layout)
            {
                id++;
                if(id % skip == 0)
                {
                    realSamples++;
                    if (modelUVs.Contains(uv))
                    {
                        hits++;
                    }
                }
            }

            float ratio = (float)hits / (float)realSamples;
            const float requiredRatio = 0.5f;

            if(ratio > requiredRatio)
            {
                anyChanges = true;
                foreach(var mesh in meshes)
                {
                    mesh.Material = mesh.Material.Replace("_a.mtrl", "_e.mtrl");
                }
            }

            return anyChanges;
        }

        private bool SkinCheckUNFConnector()
        {
            // Standard forward check.

            // For now this is unneeded, since UNF is the only mod to have been published using the _f material,
            // and has only been published on _f and no other material letter.
            return false;
        }

        /// <summary>
        /// Creates a new racial model for a given set/slot by copying from already existing racial models.
        /// </summary>
        /// <param name="setId"></param>
        /// <param name="slot"></param>
        /// <param name="newRace"></param>
        /// <returns></returns>
        public async Task AddRacialModel(int setId, string slot, XivRace newRace, string source)
        {

            var _index = new Index(_gameDirectory);
            var isAccessory = EquipmentDeformationParameterSet.SlotsAsList(true).Contains(slot);

            if (!isAccessory)
            {
                var slotOk = EquipmentDeformationParameterSet.SlotsAsList(false).Contains(slot);
                if (!slotOk)
                {
                    throw new InvalidDataException("Attempted to get racial models for invalid slot.");
                }
            }

            // If we're adding a new race, we need to clone an existing model, if it doesn't exist already.
            var format = "";
            if (!isAccessory)
            {
                format = _EquipmentModelPathFormat;
            }
            else
            {
                format = _AccessoryModelPathFormat;
            }

            var path = String.Format(format, setId.ToString().PadLeft(4, '0'), newRace.GetRaceCode(), slot);

            // File already exists, no adjustments needed.
            if ((await _index.FileExists(path))) return;

            var _eqp = new Eqp(_gameDirectory);
            var availableModels = await _eqp.GetAvailableRacialModels(setId, slot);
            var baseModelOrder = newRace.GetModelPriorityList();

            // Ok, we need to find which racial model to use as our base now...
            var baseRace = XivRace.All_Races;
            var originalPath = "";
            foreach (var targetRace in baseModelOrder)
            {
                if (availableModels.Contains(targetRace))
                {
                    originalPath = String.Format(format, setId.ToString().PadLeft(4, '0'), targetRace.GetRaceCode(), slot);
                    var exists = await _index.FileExists(originalPath);
                    if (exists)
                    {
                        baseRace = targetRace;
                        break;
                    } else
                    {
                        continue;
                    }
                }
            }

            if (baseRace == XivRace.All_Races) throw new Exception("Unable to find base model to create new racial model from.");

            // Create the new model.
            await CopyModel(originalPath, path, source);
        }

        /// <summary>
        /// Copies a given model from a previous path to a new path, including copying the materials and other down-chain items.
        /// 
        /// </summary>
        /// <param name="originalPath"></param>
        /// <param name="newPath"></param>
        /// <returns></returns>
        public async Task<long> CopyModel(string originalPath, string newPath, string source, bool copyTextures = false)
        {
            var _dat = new Dat(_gameDirectory);
            var _index = new Index(_gameDirectory);
            var _modding = new Modding(_gameDirectory);

            var fromRoot = await XivCache.GetFirstRoot(originalPath);
            var toRoot = await XivCache.GetFirstRoot(newPath);

            IItem item = null;
            if (toRoot != null)
            {
                item = toRoot.GetFirstItem();
            }

            var df = IOUtil.GetDataFileFromPath(originalPath);

            var index = await _index.GetIndexFile(df);
            var modlist = await _modding.GetModListAsync();

            var offset = index.Get8xDataOffset(originalPath);
            var xMdl = await GetRawMdlData(originalPath, false, offset);
            var model = TTModel.FromRaw(xMdl);


            if (model == null)
            {
                throw new InvalidDataException("Source model file does not exist.");
            }
            var allFiles = new HashSet<string>() { newPath };

            var originalRace = IOUtil.GetRaceFromPath(originalPath);
            var newRace = IOUtil.GetRaceFromPath(newPath);


            if(originalRace != newRace)
            {
                // Convert the model to the new race.
                ModelModifiers.RaceConvert(model, originalRace, newPath);
                ModelModifiers.FixUpSkinReferences(model, newPath);
            }

            // Language is irrelevant here.
            var _mtrl = new Mtrl(XivCache.GameInfo.GameDirectory);

            // Get all variant materials.
            var materialPaths = await GetReferencedMaterialPaths(originalPath, -1, false, false, index, modlist);

            
            var _raceRegex = new Regex("c[0-9]{4}");

            Dictionary<string, string> validNewMaterials = new Dictionary<string, string>();
            HashSet<string> copiedPaths = new HashSet<string>();
            // Update Material References and clone materials.
            foreach (var material in materialPaths)
            {

                // Get the new path.
                var path = RootCloner.UpdatePath(fromRoot, toRoot, material);

                // Adjust race code entries if needed.
                if (toRoot.Info.PrimaryType == XivItemType.equipment || toRoot.Info.PrimaryType == XivItemType.accessory)
                {
                    path = _raceRegex.Replace(path, "c" + newRace.GetRaceCode());
                }

                // Get file names.
                var io = material.LastIndexOf("/", StringComparison.Ordinal);
                var originalMatName = material.Substring(io, material.Length - io);

                io = path.LastIndexOf("/", StringComparison.Ordinal);
                var newMatName = path.Substring(io, path.Length - io);


                // Time to copy the materials!
                try
                {
                    offset = index.Get8xDataOffset(material);
                    var mtrl = await _mtrl.GetMtrlData(offset, material, 11);

                    if (copyTextures)
                    {
                        for(int i = 0; i < mtrl.TexturePathList.Count; i++)
                        {
                            var tex = mtrl.TexturePathList[i];
                            var ntex = RootCloner.UpdatePath(fromRoot, toRoot, tex);
                            if (toRoot.Info.PrimaryType == XivItemType.equipment || toRoot.Info.PrimaryType == XivItemType.accessory)
                            {
                                ntex = _raceRegex.Replace(ntex, "c" + newRace.GetRaceCode());
                            }

                            mtrl.TexturePathList[i] = ntex;

                            allFiles.Add(ntex);
                            await _dat.CopyFile(tex, ntex, source, true, item, index, modlist);
                        }
                    }

                    mtrl.MTRLPath = path;
                    allFiles.Add(mtrl.MTRLPath);
                    await _mtrl.ImportMtrl(mtrl, item, source, index, modlist);

                    if(!validNewMaterials.ContainsKey(newMatName))
                    {
                        validNewMaterials.Add(newMatName, path);
                    }
                    copiedPaths.Add(path);


                    // Switch out any material references to the material in the model file.
                    foreach (var m in model.MeshGroups)
                    {
                        if(m.Material == originalMatName)
                        {
                            m.Material = newMatName;
                        }
                    }

                } catch(Exception ex)
                {
                    // Hmmm.  The original material didn't exist.   This is pretty not awesome, but I guess a non-critical error...?
                }
            }

            if (Imc.UsesImc(toRoot) && Imc.UsesImc(fromRoot))
            {
                var _imc = new Imc(XivCache.GameInfo.GameDirectory);

                var toEntries = await _imc.GetEntries(await toRoot.GetImcEntryPaths(), false, index, modlist);
                var fromEntries = await _imc.GetEntries(await fromRoot.GetImcEntryPaths(), false, index, modlist);

                var toSets = toEntries.Select(x => x.MaterialSet).Where(x => x != 0).ToList();
                var fromSets = fromEntries.Select(x => x.MaterialSet).Where(x => x != 0).ToList();

                if(fromSets.Count > 0 && toSets.Count > 0)
                {
                    var vReplace = new Regex("/v[0-9]{4}/");

                    // Validate that sufficient material sets have been created at the destination root.
                    foreach(var mkv in validNewMaterials)
                    {
                        var validPath = mkv.Value;
                        foreach(var msetId in toSets)
                        {
                            var testPath = vReplace.Replace(validPath, "/v" + msetId.ToString().PadLeft(4, '0') + "/");
                            var copied = copiedPaths.Contains(testPath);

                            // Missing a material set, copy in the known valid material.
                            if(!copied)
                            {
                                allFiles.Add(testPath);
                                await _dat.CopyFile(validPath, testPath, source, true, item, index, modlist);
                            }
                        }
                    }
                }
            }


            // Save the final modified mdl.
            var data = await MakeNewMdlFile(model, xMdl);
            offset = await _dat.WriteModFile(data, newPath, source, item, index, modlist);

            await _index.SaveIndexFile(index);
            await _modding.SaveModListAsync(modlist);
            XivCache.QueueDependencyUpdate(allFiles.ToList());

            return offset;
        }


        public async Task FixPreDawntrailMdl(string path, string source = "Unknown")
        {
            var _dat = new Dat(XivCache.GameInfo.GameDirectory);

            // HACKHACK: This is going to be extremely inefficient, but works for the moment.
            var ttMdl = await GetModel(path);
            var xivMdl = await GetRawMdlData(path);

            var bytes = await MakeNewMdlFile(ttMdl, xivMdl);
            await _dat.WriteModFile(bytes, path, source);


        }

        /// <summary>
        /// Gets the MDL path
        /// </summary>
        /// <param name="itemModel">The item model</param>
        /// <param name="xivRace">The selected race for the given item</param>
        /// <param name="submeshId">The submesh ID - Only used for furniture items which contain multiple meshes, like the Ahriman Clock.</param>
        /// <returns>The path in string format.  Not a fucking tuple.</returns>
        public async Task<string> GetMdlPath(IItemModel itemModel, XivRace xivRace, string submeshId = null)
        {
            string mdlFolder = "", mdlFile = "";

            var mdlInfo = itemModel.ModelInfo;
            var id = mdlInfo.PrimaryID.ToString().PadLeft(4, '0');
            var bodyVer = mdlInfo.SecondaryID.ToString().PadLeft(4, '0');
            var itemCategory = itemModel.SecondaryCategory;

            var race = xivRace.GetRaceCode();
            var itemType = itemModel.GetPrimaryItemType();

            switch (itemType)
            {
                case XivItemType.equipment:
                    mdlFolder = $"chara/{itemType}/e{id}/model";
                    mdlFile = $"c{race}e{id}_{itemModel.GetItemSlotAbbreviation()}{MdlExtension}";
                    break;
                case XivItemType.accessory:
                    mdlFolder = $"chara/{itemType}/a{id}/model";
                    var abrv = itemModel.GetItemSlotAbbreviation();
                    // Just left ring things.
                    if (submeshId == "ril")
                    {
                        abrv = "ril";
                    }
                    mdlFile = $"c{race}a{id}_{abrv}{MdlExtension}";
                    break;
                case XivItemType.weapon:
                    mdlFolder = $"chara/{itemType}/w{id}/obj/body/b{bodyVer}/model";
                    mdlFile = $"w{id}b{bodyVer}{MdlExtension}";
                    break;
                case XivItemType.monster:
                    mdlFolder = $"chara/{itemType}/m{id}/obj/body/b{bodyVer}/model";
                    mdlFile = $"m{id}b{bodyVer}{MdlExtension}";
                    break;
                case XivItemType.demihuman:
                    mdlFolder = $"chara/{itemType}/d{id}/obj/equipment/e{bodyVer}/model";
                    mdlFile = $"d{id}e{bodyVer}_{SlotAbbreviationDictionary[itemModel.TertiaryCategory]}{MdlExtension}";
                    break;
                case XivItemType.human:
                    if (itemCategory.Equals(XivStrings.Body))
                    {
                        mdlFolder = $"chara/{itemType}/c{race}/obj/body/b{bodyVer}/model";
                        mdlFile = $"c{race}b{bodyVer}_{SlotAbbreviationDictionary[itemModel.TertiaryCategory]}{MdlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Hair))
                    {
                        mdlFolder = $"chara/{itemType}/c{race}/obj/hair/h{bodyVer}/model";
                        mdlFile = $"c{race}h{bodyVer}_{SlotAbbreviationDictionary[itemCategory]}{MdlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Face))
                    {
                        mdlFolder = $"chara/{itemType}/c{race}/obj/face/f{bodyVer}/model";
                        mdlFile = $"c{race}f{bodyVer}_{SlotAbbreviationDictionary[itemCategory]}{MdlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Tail))
                    {
                        mdlFolder = $"chara/{itemType}/c{race}/obj/tail/t{bodyVer}/model";
                        mdlFile = $"c{race}t{bodyVer}_{SlotAbbreviationDictionary[itemCategory]}{MdlExtension}";
                    }
                    else if (itemCategory.Equals(XivStrings.Ear))
                    {
                        mdlFolder = $"chara/{itemType}/c{race}/obj/zear/z{bodyVer}/model";
                        mdlFile = $"c{race}z{bodyVer}_zer{MdlExtension}";
                    }
                    break;
                case XivItemType.furniture:
                    // Language doesn't matter for this call.
                    var housing = new Housing(_gameDirectory, XivLanguage.None);
                    var mdlPath = "";
                    // Housing assets use a different function to scrub the .sgd files for
                    // their direct absolute model references.
                    var assetDict = await housing.GetFurnitureModelParts(itemModel);

                    if (submeshId == null || submeshId == "base")
                    {
                        submeshId = "b0";
                    }

                    mdlPath = assetDict[submeshId];
                    return mdlPath;
                    break;
                default:
                    mdlFolder = "";
                    mdlFile = "";
                    break;
            }

            return mdlFolder + "/" + mdlFile;
        }

        public static readonly Dictionary<string, string> SlotAbbreviationDictionary = new Dictionary<string, string>
        {
            {XivStrings.Head, "met"},
            {XivStrings.Hands, "glv"},
            {XivStrings.Legs, "dwn"},
            {XivStrings.Feet, "sho"},
            {XivStrings.Body, "top"},
            {XivStrings.Earring, "ear"},
            {XivStrings.Ear, "zer"},
            {XivStrings.Neck, "nek"},
            {XivStrings.Rings, "rir"},
            {XivStrings.LeftRing, "ril"},
            {XivStrings.Wrists, "wrs"},
            {XivStrings.Head_Body, "top"},
            {XivStrings.Body_Hands, "top"},
            {XivStrings.Body_Hands_Legs, "top"},
            {XivStrings.Body_Legs_Feet, "top"},
            {XivStrings.Body_Hands_Legs_Feet, "top"},
            {XivStrings.Legs_Feet, "dwn"},
            {XivStrings.All, "top"},
            {XivStrings.Face, "fac"},
            {XivStrings.Iris, "iri"},
            {XivStrings.Etc, "etc"},
            {XivStrings.Accessory, "acc"},
            {XivStrings.Hair, "hir"},
            {XivStrings.Tail, "til"},

        };

        private static readonly Dictionary<byte, VertexDataType> VertexTypeDictionary =
            new Dictionary<byte, VertexDataType>
            {
                {0x0, VertexDataType.Float1},
                {0x1, VertexDataType.Float2},
                {0x2, VertexDataType.Float3},
                {0x3, VertexDataType.Float4},
                {0x5, VertexDataType.Ubyte4},
                {0x6, VertexDataType.Short2},
                {0x7, VertexDataType.Short4},
                {0x8, VertexDataType.Ubyte4n},
                {0x9, VertexDataType.Short2n},
                {0xA, VertexDataType.Short4n},
                {0xD, VertexDataType.Half2}, // XXX: These were originally added here with the wrong values
                {0xE, VertexDataType.Half4}, //      Keeping them here in case someone was relying on it
                {0xF, VertexDataType.Half2},
                {0x10, VertexDataType.Half4},
                {0x11, VertexDataType.Unknown17} // XXX: Bone weights use this type, not sure what it is
            };

        private static readonly Dictionary<byte, VertexUsageType> VertexUsageDictionary =
            new Dictionary<byte, VertexUsageType>
            {
                {0x0, VertexUsageType.Position },
                {0x1, VertexUsageType.BoneWeight },
                {0x2, VertexUsageType.BoneIndex },
                {0x3, VertexUsageType.Normal },
                {0x4, VertexUsageType.TextureCoordinate },
                {0x5, VertexUsageType.Tangent },
                {0x6, VertexUsageType.Binormal },
                {0x7, VertexUsageType.Color }
            };

        private class ColladaMeshData
        {
            public MeshGeometry3D MeshGeometry { get; set; }

            public List<byte[]> BoneIndices { get; set; }

            public List<byte[]> BoneWeights { get; set; }

            public List<int> Handedness { get; set; } = new List<int>();

            public Vector2Collection TextureCoordintes1 { get; set; }

            public Vector3Collection VertexColors { get; set; } = new Vector3Collection();

            public List<float> VertexAlphas { get; set; } = new List<float>();

            public Dictionary<int, int> PartsDictionary { get; set; }

            public Dictionary<int, List<string>> PartBoneDictionary { get; set; } = new Dictionary<int, List<string>>();

            public Dictionary<string, int> BoneNumDictionary;
        }

        /// <summary>
        /// This class holds the imported data after its been converted to bytes
        /// </summary>
        private class VertexByteData
        {
            public List<byte> VertexData0 { get; set; } = new List<byte>();

            public List<byte> VertexData1 { get; set; } = new List<byte>();

            public List<byte> IndexData { get; set; } = new List<byte>();

            public int VertexCount { get; set; }

            public int IndexCount { get; set; }
        }

        private class VertexDataSection
        {
            public int CompressedVertexDataBlockSize { get; set; }

            public int CompressedIndexDataBlockSize { get; set; }

            public int VertexDataBlockPartCount { get; set; }

            public int IndexDataBlockPartCount { get; set; }

            public List<byte> VertexDataBlock = new List<byte>();

            public List<byte> IndexDataBlock = new List<byte>();
        }
    }
}
