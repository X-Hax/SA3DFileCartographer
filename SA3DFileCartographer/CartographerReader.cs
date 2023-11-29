using SA3D.Common.IO;
using SA3D.SA2Event.Animation;
using SA3D.SA2Event.Model;
using SA3D.Modeling.File;
using SA3D.Modeling.Mesh;
using SA3D.Modeling.Mesh.Basic;
using SA3D.Modeling.Mesh.Chunk;
using SA3D.Modeling.Mesh.Gamecube;
using SA3D.Modeling.ObjectData;
using SA3D.Modeling.Animation;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Diagnostics;
using System.Text;
using SA3D.Modeling.Mesh.Gamecube.Parameters;
using SA3D.Texturing.Texname;
using System.Collections.Generic;

namespace SA3DFileCarthographer
{
    internal class CartographerReader : EndianStackReader
    {
        private readonly int _bytesPerPixel;
        private readonly byte[] _pixels;

        public uint LowestRead { get; set; }

        private static readonly (string? displayName, string typename, Rgba32 color, Rgba32 textColor)[] _mapping = new (string?, string, Rgba32, Rgba32)[]
        {
            (null, typeof(Program).FullName!, Color.Black, Color.Black),

            ("GC poly", typeof(GCPolygon).FullName!, Color.PowderBlue, Color.Black),
            (null, typeof(GCParameterExtensions).FullName!, Color.SlateBlue, Color.Black),
            ("GC Parameter", typeof(IGCParameter).FullName!, Color.SlateBlue, Color.Black),
            ("GC Mesh", typeof(GCMesh).FullName!, Color.CornflowerBlue, Color.Black),
            ("GC Vertices", typeof(GCVertexSet).FullName!, Color.DeepSkyBlue, Color.Black),
            ("GC Attach", typeof(GCAttach).FullName!, Color.SkyBlue, Color.Black),

            ("Poly Chunk", typeof(PolyChunk).FullName!, Color.MediumBlue, Color.White),
            ("Vertex Chunk", typeof(VertexChunk).FullName!, Color.DarkBlue, Color.White),
            ("CHUNK Attach", typeof(ChunkAttach).FullName!, Color.SteelBlue, Color.Black),

            ("BASIC Attach", typeof(BasicAttach).FullName!, Color.DarkCyan, Color.Black),

            ("Attach", typeof(Attach).FullName!, Color.Blue, Color.White),

            ("UV Anim", typeof(SurfaceAnimationData).FullName!, Color.RebeccaPurple, Color.Black),
            ("Keyframe body", "SA3D.Modeling.Animation.Utilities.KeyframeRead", Color.DeepPink, Color.Black),
            ("Keyframe head", typeof(Keyframes).FullName!, Color.MediumPurple, Color.Black),
            ("Motion", typeof(Motion).FullName!, Color.Purple, Color.White),

            ("Land Entry", typeof(LandEntry).FullName!, Color.GreenYellow, Color.Black),
            ("Node", typeof(Node).FullName!, Color.LimeGreen, Color.Black),

            ("Tex List", typeof(TextureNameList).FullName!, Color.Gray, Color.Black),
            ("Event BtC", typeof(BigTheCatEntry).FullName!, Color.ForestGreen, Color.Black),
            ("Event Entry", typeof(EventEntry).FullName!, Color.GreenYellow, Color.Black),
            ("Event Scene", typeof(Scene).FullName!, Color.OrangeRed, Color.Black),

            ("Meta data", typeof(MetaData).FullName!, Color.Orange, Color.Black),
            ("File Header", typeof(ModelData).FullName!, Color.Red, Color.Black),
            (null, typeof(AnimationFile).FullName!, Color.Red, Color.Black),
            (null, typeof(LevelFile).FullName!, Color.Red, Color.Black),
            (null, typeof(ModelFile).FullName!, Color.Red, Color.Black)
        };

        private static readonly Dictionary<string, byte> _mappingDictionary;

        static CartographerReader()
        {
            _mappingDictionary = new();

            for(int i = 1; i < _mapping.Length; i++)
            {
                (_, string typename, _, _) = _mapping[i];
                _mappingDictionary.Add(typename, (byte)i);
            }
        }

        public CartographerReader(byte[] source, int bytesPerPixel = 4, uint imageBase = 0, bool bigEndian = false) : base(source, imageBase, bigEndian)
        {
            _bytesPerPixel = bytesPerPixel;
            _pixels = new byte[(int)MathF.Ceiling(source.Length / (float)bytesPerPixel)];
        }

        public void Map(uint address, int length)
        {
            if(address < LowestRead)
            {
                LowestRead = address;
            }

            StackFrame[] frames = new StackTrace().GetFrames();
            for(int i = 2; i < frames.Length; i++)
            {
                string? typeName = frames[i].GetMethod()?.DeclaringType?.FullName;
                if(typeName == null || !_mappingDictionary.TryGetValue(typeName, out byte pixel))
                {
                    continue;
                }

                int pixelIndex = (int)(address / _bytesPerPixel);
                int pixels = Math.Max(1, (int)MathF.Ceiling(length / (float)_bytesPerPixel));

                for(int j = 0; j < pixels; j++)
                {
                    _pixels[pixelIndex + j] = pixel;
                }

                return;
            }
        }

        public void OutputImage(string filepath)
        {
            int width = Math.Min(_pixels.Length, 256);
            int height = (int)MathF.Ceiling(_pixels.Length / 256f);

            Rgba32[] colors = new Rgba32[width * height];
            for(int i = 0; i < _pixels.Length; i++)
            {
                colors[i] = _mapping[_pixels[i]].color;
            }

            for(int i = _pixels.Length; i < colors.Length; i++)
            {
                colors[i] = Color.Transparent;
            }

            Image<Rgba32> image = Image.LoadPixelData<Rgba32>(colors, width, height);

            if(image.Height < 352)
            {
                height = 352;

                image.Mutate(x => x.Resize(new ResizeOptions()
                {
                    Position = AnchorPositionMode.TopLeft,
                    Mode = ResizeMode.Pad,
                    Size = new Size(width, 352)
                }));
            }

            image.Mutate(x =>
            {
                x = x.Resize(new ResizeOptions()
                {
                    Position = AnchorPositionMode.TopLeft,
                    Mode = ResizeMode.Pad,
                    Size = new Size(width + 100, height)
                });

                int location = 2;
                FontCollection collection = new();

                Font font = SystemFonts.CreateFont("Cascadia Code", 12);

                foreach((string? displayName, _, Rgba32 color, Rgba32 textColor) in _mapping)
                {
                    if(displayName == null)
                    {
                        continue;
                    }

                    x = x.Fill(color, new Rectangle(257, location - 2, 100, 16));
                    x = x.DrawText(displayName, font, textColor, new PointF(260, location));
                    location += 16;
                }
            });


            image.SaveAsPng(filepath);
        }

        #region Overriden read methods

        public override byte this[uint index]
        {
            get
            {
                Map(index, 1);
                return base[index];
            }
        }

        public override byte[] ReadBytes(uint address, int length)
        {
            if(length > 0)
            {
                Map(address, length);
            }

            return base.ReadBytes(address, length);
        }

        public override void ReadBytes(uint sourceAddress, byte[] destination, uint destinationAddress, int length)
        {
            Map(sourceAddress, length);
            base.ReadBytes(sourceAddress, destination, destinationAddress, length);
        }

        public override byte ReadByte(uint address)
        {
            Map(address, 1);
            return base.ReadByte(address);
        }

        public override sbyte ReadSByte(uint address)
        {
            Map(address, 1);
            return base.ReadSByte(address);
        }

        public override short ReadShort(uint address)
        {
            Map(address, 2);
            return base.ReadShort(address);
        }

        public override ushort ReadUShort(uint address)
        {
            Map(address, 2);
            return base.ReadUShort(address);
        }

        public override int ReadInt(uint address)
        {
            Map(address, 4);
            return base.ReadInt(address);
        }

        public override uint ReadUInt(uint address)
        {
            Map(address, 4);
            return base.ReadUInt(address);
        }

        public override long ReadLong(uint address)
        {
            Map(address, 8);
            return base.ReadLong(address);
        }

        public override ulong ReadULong(uint address)
        {
            Map(address, 8);
            return base.ReadULong(address);
        }

        public override float ReadFloat(uint address)
        {
            Map(address, 4);
            return base.ReadFloat(address);
        }

        public override double ReadDouble(uint address)
        {
            Map(address, 8);
            return base.ReadDouble(address);
        }

        public override string ReadString(uint address, Encoding encoding, uint count)
        {
            if(count > 0)
            {
                Map(address, (int)count);
            }

            return base.ReadString(address, encoding, count);
        }

        public override ReadOnlySpan<byte> Slice(int address, int length)
        {
            if(length > 0)
            {
                Map((uint)address, length);
            }

            return base.Slice(address, length);
        }

        #endregion

    }
}
