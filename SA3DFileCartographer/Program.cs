using SA3D.Archival;
using SA3D.Common.IO;
using SA3D.Modeling.File;
using SA3D.Modeling.Mesh;
using SA3D.Modeling.ObjectData;
using SA3D.Modeling.ObjectData.Enums;
using SA3D.Modeling.Structs;
using SA3D.SA2Event;
using SA3D.SA2Event.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SA3DFileCarthographer
{
    public static class Program
    {
        private enum FileFormat
        {
            Model,
            Level,
            Motion,
            Event,
            AllEvents
        }

        public static void Main(string[] args)
        {
            if(args.Length == 0)
            {
                Console.WriteLine("Please specify a filepath! For more info, enter ? after the .exe");
                return;
            }

            if(args[0] == "?" || args[0].StartsWith("-"))
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string resourceName = "SA3DFileCarthographer.cmdhelp.txt";
                using(Stream stream = assembly.GetManifestResourceStream(resourceName) ?? throw new NullReferenceException()) // error cannot happen tho
                {
                    Console.WriteLine(new StreamReader(stream).ReadToEnd());
                }

                return;
            }

            FileFormat? format = null;
            bool extractModels = false;
            bool mapEventMotions = false;
            bool decompressEvent = false;
            string? outpath = null;

            for(int i = 1; i < args.Length; i++)
            {
                switch(args[i].ToLower())
                {
                    case "-fmt":
                    case "--format":
                        i++;
                        string formatName = args[i];

                        switch(formatName)
                        {
                            case "MDL":
                                format = FileFormat.Model;
                                break;
                            case "LVL":
                                format = FileFormat.Level;
                                break;
                            case "MTN":
                                format = FileFormat.Motion;
                                break;
                            case "EVE":
                                format = FileFormat.Event;
                                break;
                            case "ALLEVE":
                                format = FileFormat.AllEvents;
                                break;
                        }

                        break;
                    case "-out":
                        i++;
                        outpath = args[i];
                        break;
                    case "--model-extract":
                    case "-mxt":
                        extractModels = true;
                        break;
                    case "-mem":
                    case "--map-event-motions":
                        mapEventMotions = true;
                        break;
                    case "-dce":
                    case "--decompress-event":
                        decompressEvent = true;
                        break;
                }
            }

            string path = Path.Combine(Environment.CurrentDirectory, args[0]);
            if(format != FileFormat.AllEvents)
            {
                if(!File.Exists(path))
                {
                    Console.WriteLine("Path does not lead to a file! enter --help for more info");
                    return;
                }
            }
            else if(!Directory.Exists(path))
            {
                Console.WriteLine("Path does not lead to a directory! enter --help for more info");
                return;
            }

            outpath ??= path + ".png";

            if(format == FileFormat.Event)
            {
                CartographEvent(path, extractModels, mapEventMotions, decompressEvent, outpath);
            }
            else if(format == FileFormat.AllEvents)
            {
                CartographAllEvents(path, extractModels, mapEventMotions, decompressEvent, outpath);
            }
            else
            {
                format ??= CheckFileFormat(path);
                Cartograph(path, format.Value, outpath);
            }
        }

        private static FileFormat CheckFileFormat(string filepath)
        {
            byte[] source = File.ReadAllBytes(filepath);

            if(ModelFile.CheckIsModelFile(source))
            {
                return FileFormat.Model;
            }
            else if(LevelFile.CheckIsLevelFile(source))
            {
                return FileFormat.Level;
            }
            else if(AnimationFile.CheckIsAnimationFile(source))
            {
                return FileFormat.Motion;
            }

            throw new InvalidDataException("Cannot deduce file format from source! Is it perhaps an event?");    
            
        }

        private static void Cartograph(string path, FileFormat format, string outpath)
        {
            byte[] data = File.ReadAllBytes(path);
            using(CartographerReader reader = new(data))
            {
                switch(format)
                {
                    case FileFormat.Model:
                        ModelFile.Read(reader);
                        break;
                    case FileFormat.Level:
                        LevelFile.Read(reader);
                        break;
                    case FileFormat.Motion:
                        AnimationFile.Read(reader);
                        break;
                }

                reader.OutputImage(outpath);
            }

        }

        private static void CartographAllEvents(string directory, bool extractModels, bool mapMotions, bool decompress, string outDir)
        {
            foreach(string filepath in Directory.GetFiles(directory, "e????.prs"))
            {
                string filename = Path.GetFileNameWithoutExtension(filepath);
                string fileOutDir = Path.Join(outDir, filename);
                string outpath = Path.Join(fileOutDir, "mapping.png");
                Directory.CreateDirectory(fileOutDir);
                CartographEvent(filepath, extractModels, mapMotions, decompress, outpath);
                Console.WriteLine("Finished " + filename);
            }
        }

        private static void CartographEvent(string path, bool extractModels, bool mapMotions, bool decompress, string outpath)
        {
            string motionFilePath = path[..^4] + "motion.bin";

            EndianStackReader? motionReader = null;
            if(File.Exists(motionFilePath))
            {
                byte[] motionData = File.ReadAllBytes(motionFilePath);

                motionReader = mapMotions 
                    ? new CartographerReader(motionData, bigEndian: true) 
                    : new EndianStackReader(motionData, bigEndian: true);
            }

            CartographerReader data = new(PRS.DecompressPRS(File.ReadAllBytes(path)));

            if(decompress)
            {
                File.WriteAllBytes(Path.ChangeExtension(outpath, ".prs"), data.Source.ToArray());
            }

            PointerLUT lut = new();
            ModelData.Read(data, motionReader, lut);
            data.OutputImage(outpath);

            if(motionReader is CartographerReader motionOut)
            {
                motionOut.OutputImage(Path.ChangeExtension(outpath, "motion.png"));
            }

            if(extractModels)
            {
                ScavengeEventForModel(path, lut, outpath);
            }
        }

        private static void ScavengeEventForModel(string filepath, PointerLUT eventLut, string outpath)
        {
            CartographerReader reader = new(PRS.DecompressPRS(File.ReadAllBytes(filepath)));
            EventType type = ModelData.EvaluateEventType(reader);
            reader.PushBigEndian(type.GetBigEndian());
            reader.ImageBase = type.GetMainImageBase();

            uint texlistAddr = reader.ReadPointer(4);
            uint texlistNamesAddr = reader.ReadPointer(texlistAddr);

            PointerLUT lut = new();
            uint curLookatAddr = texlistNamesAddr - Node.StructSize;
            reader.LowestRead = (uint)reader.Source.Length;
            while(true)
            {
                // figure out if the node we are looking at is a gc event
                // node or a regular one, as gc event nodes are 4 bytes longer
                // for that, we check the "skip children" flag. if it has a child,
                // then no flag should be set but the child pointer should and vise versa
                bool found = false;
                for(int i = 0; i < 2; i++, curLookatAddr -= 4)
                {
                    uint objFlags = reader.ReadUInt(curLookatAddr);
                    if(objFlags >= 0x100)
                    {
                        continue;
                    }

                    NodeAttributes attributes = (NodeAttributes)objFlags;
                    uint childAddr = reader.ReadUInt(curLookatAddr + 44);
                    if((attributes.HasFlag(NodeAttributes.SkipChildren) && childAddr != 0)
                        || (!attributes.HasFlag(NodeAttributes.SkipChildren) && childAddr < reader.ImageBase))
                    {
                        continue;
                    }

                    Node.Read(reader, curLookatAddr, i == 0 ? ModelFormat.SA2 : ModelFormat.SA2B, lut);
                    found = true;
                    break;
                }

                if(!found)
                {
                    break;
                }

                curLookatAddr = reader.LowestRead - Node.StructSize;
                if(curLookatAddr < 44 || curLookatAddr >= texlistNamesAddr)
                {
                    break;
                }
            }

            reader.OutputImage(Path.ChangeExtension(outpath, $"unusedModels.png"));

            foreach(KeyValuePair<uint, Node> item in lut.Nodes.GetDictFrom())
            {
                if(item.Value.Parent != null || eventLut.Nodes.TryGetValue(item.Key, out _))
                {
                    continue;
                }

                string extension = "";
                extension = item.Value.GetAttachFormat() switch
                {
                    AttachFormat.BASIC => ".sa1mdl",
                    AttachFormat.CHUNK => ".sa2mdl",
                    AttachFormat.GC => ".sa2bmdl",
                    _ => ".bufmdl",
                };
                string modelPath = Path.ChangeExtension(outpath, $"{item.Key:X8}{extension}");
                ModelFile.WriteToFile(modelPath, item.Value);
            }

            reader.PopEndian();
        }
    }
}
