﻿// COPYRIGHT 2014 by the Open Rails project.
// 
// This file is part of Open Rails.
// 
// Open Rails is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Open Rails is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Open Rails.  If not, see <http://www.gnu.org/licenses/>.

// This file is the responsibility of the 3D & Environment Team. 

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Orts.Viewer3D.Common;
using ORTS.Common;
using glTFLoader.Schema;

namespace Orts.Viewer3D
{
    public class GltfAnimation
    {
        public List<GltfAnimationChannel> Channels = new List<GltfAnimationChannel>();

        public GltfAnimation() { }
    }
    
    public class GltfAnimationChannel
    {
        public int TargetNode; // ref to index in hierarchy, e.g. Matrices(index)
        public AnimationChannelTarget.PathEnum Path; // e.g. rotation or tcb_rot, translation or linear_pos, scale, tcb_pos
        public AnimationSampler.InterpolationEnum Interpolation;
        public float[] TimeArray;
        public float TimeMin;
        public float TimeMax;
        public Vector3[] OutputVector3;
        public Quaternion[] OutputQuaternion;
        public float[] OutputWeights;
        public int FrameCount;
    }

    public partial class PoseableShape
    {
        protected void AnimateOneGltfMatrix(int iMatrix, float key)
        {
            if (!(SharedShape is GltfShape shape))
                return;

            if (shape.GltfAnimations == null || shape.GltfAnimations.Count == 0)
            {
                if (!SeenShapeAnimationError.ContainsKey(shape.FilePath))
                    Trace.TraceInformation("Ignored missing animations data in shape {0}", shape.FilePath);
                SeenShapeAnimationError[shape.FilePath] = true;
                return;  // animation is missing
            }

            if (iMatrix < 0 || iMatrix >= shape.GltfAnimations[0].Channels.Count() || iMatrix >= XNAMatrices.Length)
            {
                if (!SeenShapeAnimationError.ContainsKey(shape.FilePath))
                    Trace.TraceInformation("Ignored out of bounds matrix {1} in shape {0}", shape.FilePath, iMatrix);
                SeenShapeAnimationError[shape.FilePath] = true;
                return;  // mismatched matricies
            }

            var channels = shape.GltfAnimations[0].Channels.Where(ch => ch.TargetNode == iMatrix);
            if (!channels.Any())
                return;

            // Time is in seconds.
            var time = key;

            Matrix scaleM = new Matrix();
            Matrix rotationM = new Matrix();
            Matrix translationM = new Matrix();
            bool s = false, r = false, t = false;

            foreach (var channel in channels)
            {
                // Interpolating between two frames
                var index = 0;
                for (var i = 0; i < channel.TimeArray.Length; i++)
                    if (channel.TimeArray[i] <= time)
                        index = i;
                    else if (channel.TimeArray[i] > time)
                        break;
                
                var frame1 = index;
                var frame2 = Math.Min(channel.TimeArray.Length - 1, frame1 + 1);
                var time1 = channel.TimeArray[frame1];
                var time2 = channel.TimeArray[frame2];

                var amount = 0.0f;
                var m = Matrix.Identity;
                switch (channel.Interpolation)
                {
                    case AnimationSampler.InterpolationEnum.LINEAR: amount = time1 < time2 ? MathHelper.Clamp((time - time1) / (time2 - time1), 0, 1) : 0; break;
                    case AnimationSampler.InterpolationEnum.STEP: amount = 0; break;
                    case AnimationSampler.InterpolationEnum.CUBICSPLINE: // TODO
                    case AnimationSampler.InterpolationEnum.CATMULLROMSPLINE: // TODO
                    default: amount = 0; break;
                }
                switch (channel.Path)
                {
                    case AnimationChannelTarget.PathEnum.translation:
                        var outputT = Vector3.Lerp(channel.OutputVector3[frame1], channel.OutputVector3[frame2], amount);
                        translationM = Matrix.CreateTranslation(outputT);
                        t = true;
                        break;
                    case AnimationChannelTarget.PathEnum.rotation:
                        var outputR = Quaternion.Slerp(channel.OutputQuaternion[frame1], channel.OutputQuaternion[frame2], amount);
                        rotationM = Matrix.CreateFromQuaternion(outputR);
                        r = true;
                        break;
                    case AnimationChannelTarget.PathEnum.scale:
                        var outputS = Vector3.Lerp(channel.OutputVector3[frame1], channel.OutputVector3[frame2], amount);
                        scaleM = Matrix.CreateScale(outputS);
                        s = true;
                        break;
                    case AnimationChannelTarget.PathEnum.weights:
                    default: break;
                }
            }
            shape.Matrices[iMatrix].Decompose(out var scale, out var rotation, out var translation);
            if (!s) scaleM = Matrix.CreateScale(scale);
            if (!r) rotationM = Matrix.CreateFromQuaternion(rotation);
            if (!t) translationM = Matrix.CreateTranslation(translation);

            XNAMatrices[iMatrix] = scaleM * rotationM * translationM;
        }
    }
    
    public class GltfShape : SharedShape
    {
        public static List<string> ExtensionsSupported = new List<string>
        {
            "MSFT_texture_dds",
        };

        class MSFT_texture_dds
        {
            public int Source { get; set; }
        }

        class MSFT_lod
        {
            public int[] Ids { get; set; }
        }

        string FileDir { get; set; }
        int SkeletonRootNode;

        /// <summary>
        /// glTF specification declares that the model's forward is +Z. However OpenRails uses -Z as forward,
        /// so we need to apply a 180 degree rotation to turn around every model matrix to conform the spec.
        /// </summary>
        static Matrix PlusZToForward = Matrix.CreateFromAxisAngle(Vector3.UnitY, MathHelper.Pi);
        static readonly string[] StandardTextureExtensionFilter = new[] { ".png", ".jpg", ".jpeg" };
        static readonly string[] DdsTextureExtensionFilter = new[] { ".dds" };

        public Dictionary<int, GltfAnimation> GltfAnimations = new Dictionary<int, GltfAnimation>();
        public Dictionary<string, VertexBufferBinding> VertexBuffers = new Dictionary<string, VertexBufferBinding>();

        public List<int> SceneNodes = new List<int>(); // [hierarchyIndex] = nodeNumber. Theoretically this is not needed, just for debug.

        /// <summary>
        /// glTF shape from file
        /// </summary>
        /// <param name="viewer"></param>
        /// <param name="filePath">Path to shape's glTF file</param>
        public GltfShape(Viewer viewer, string filePath)
            : base(viewer, filePath)
        {
            // In glTF the animation frames are measured in seconds, so the default FPS value must be 1 second per second.
            CustomAnimationFPS = 1;
        }

        protected override void LoadContent()
        {
            Trace.Write("G");
            var textureFlags = Helpers.TextureFlags.None;

            Dictionary<int, string> externalLods = new Dictionary<int, string>();

            FileDir = Path.GetDirectoryName(FilePath);
            var inputFilename = Path.GetFileNameWithoutExtension(FilePath).ToUpper();
            if (inputFilename.Contains("_LOD"))
                inputFilename = inputFilename.Substring(0, inputFilename.Length - 6); // to strip the "_LOD00" from the end
            var files = Directory.GetFiles(FileDir);
            Match match;
            foreach (var file in files)
            {
                if ((match = Regex.Match(Path.GetFileName(file.ToUpper()), inputFilename + @"_LOD(\d\d).GLTF$")).Success)
                {
                    if (int.TryParse(match.Groups[1].Value, out var lod))
                        externalLods[lod] = file;
                }
            }

            if (!externalLods.Any())
                externalLods.Add(0, FilePath);
            LodControls = new[] { new GltfLodControl(this, externalLods) };
        }

        public override Matrix SetRenderMatrices(ShapePrimitive baseShapePrimitive, Matrix[] animatedMatrices, ref Matrix tileTranslation, out Matrix[] bones)
        {
            var shapePrimitive = baseShapePrimitive as GltfPrimitive;
            bones = Enumerable.Repeat(Matrix.Identity, Math.Min(SceneryShader.MAX_BONES, shapePrimitive.Joints.Length)).ToArray();
            for (var j = 0; j < bones.Length; j++)
            {
                bones[j] = shapePrimitive.InverseBindMatrices[j];
                var hi = shapePrimitive.Joints[j];
                while (hi >= 0 && hi < shapePrimitive.Hierarchy.Length)
                {
                    Matrix.Multiply(ref bones[j], ref animatedMatrices[hi], out bones[j]);
                    hi = shapePrimitive.Hierarchy[hi];
                }
                Matrix.Multiply(ref bones[j], ref PlusZToForward, out bones[j]);
                Matrix.Multiply(ref bones[j], ref tileTranslation, out bones[j]);
            }

            // Skinned primitive
            if (shapePrimitive.Joints.Length > 1)
                return Matrix.Identity;

            // Non-skinned primitive
            var matrix = bones[0];
            bones = null;
            return matrix;
        }

        static internal Func<BinaryReader, float> GetNormalizedReader(Accessor.ComponentTypeEnum componentType)
        {
            switch (componentType)
            {
                case Accessor.ComponentTypeEnum.UNSIGNED_BYTE: return (br) => br.ReadByte() / 255.0f;
                case Accessor.ComponentTypeEnum.UNSIGNED_SHORT: return (br) => br.ReadUInt16() / 65535.0f;
                case Accessor.ComponentTypeEnum.BYTE: return (br) => Math.Max(br.ReadSByte() / 127.0f, -1.0f);
                case Accessor.ComponentTypeEnum.SHORT: return (br) => Math.Max(br.ReadInt16() / 32767.0f, -1.0f);
                case Accessor.ComponentTypeEnum.FLOAT:
                default: return (br) => br.ReadSingle();
            }
        }

        static internal Func<BinaryReader, ushort> GetIntegerReader(Accessor.ComponentTypeEnum componentTypeEnum)
        {
            switch (componentTypeEnum)
            {
                case Accessor.ComponentTypeEnum.UNSIGNED_INT: return (br) => (ushort)br.ReadUInt32();
                case Accessor.ComponentTypeEnum.UNSIGNED_BYTE: return (br) => br.ReadByte();
                case Accessor.ComponentTypeEnum.UNSIGNED_SHORT:
                default: return (br) => br.ReadUInt16();
            }
        }

        public class GltfLodControl : LodControl
        {
            Dictionary<string, Gltf> Gltfs = new Dictionary<string, Gltf>();
            List<GltfDistanceLevel> GltfDistanceLevels = new List<GltfDistanceLevel>();

            public GltfLodControl(GltfShape shape, Dictionary<int, string> externalLods)
            {
                foreach (var id in externalLods.Keys)
                {
                    var gltfFile = glTFLoader.Interface.LoadModel(externalLods[id]);
                    Gltfs.Add(externalLods[id], gltfFile);

                    if (gltfFile.ExtensionsRequired != null)
                    {
                        var unsupportedExtensions = new List<string>();
                        foreach (var extensionRequired in gltfFile.ExtensionsRequired)
                            if (!ExtensionsSupported.Contains(extensionRequired))
                                unsupportedExtensions.Add($"\"{extensionRequired}\"");
                        if (unsupportedExtensions.Any())
                            Trace.TraceWarning($"glTF required extension {string.Join(", ", unsupportedExtensions)} is unsupported in file {externalLods[id]}");
                    }

                    // TODO: if no multiple external lods, then check the scene node for MSFT_lod extension, and create the internal lods
                    GltfDistanceLevels.Add(new GltfDistanceLevel(shape, id, gltfFile, externalLods[id]));
                }
                DistanceLevels = GltfDistanceLevels.ToArray();
            }
        }

        public class GltfDistanceLevel : DistanceLevel
        {
            // See the glTF specification at https://www.khronos.org/registry/glTF/specs/2.0/glTF-2.0.html
            Gltf Gltf;
            string GltfDir;
            float MinimumScreenCoverage;
            internal Dictionary<int, Matrix[]> AllInverseBindMatrices = new Dictionary<int, Matrix[]>();
            public Matrix[] Matrices = new Matrix[0];
            Viewer Viewer;

            static readonly Stack<int> TempStack = new Stack<int>(); // (nodeNumber, parentIndex)

            public GltfDistanceLevel(GltfShape shape, int lodId, Gltf gltfFile, string gltfFileName)
            {
                ViewingDistance = float.MaxValue; // glTF is using screen coverage, so this one is set for not getting into the way accidentally
                ViewSphereRadius = 100;
                var morphWarning = false;

                Viewer = shape.Viewer;

                Gltf = gltfFile;
                GltfDir = Path.GetDirectoryName(gltfFileName);

                var meshes = new Dictionary<int, Node>();
                TempStack.Clear();
                Array.ForEach(gltfFile.Scenes.ElementAtOrDefault(gltfFile.Scene ?? 0).Nodes, node => TempStack.Push(node));
                var hierarchy = Enumerable.Repeat(-1, gltfFile.Nodes.Length).ToArray();
                var matrices = Enumerable.Repeat(Matrix.Identity, gltfFile.Nodes.Length).ToArray();
                var matrixnames = Enumerable.Repeat("", gltfFile.Nodes.Length).ToArray();
                var parents = new Dictionary<int, int>();
                var lods = Enumerable.Repeat(-1, gltfFile.Nodes.Length).ToArray(); // -1: common; 0, 1, 3, etc.: the lod the node belongs to
                while (TempStack.Any())
                {
                    var nodeNumber = TempStack.Pop();
                    var node = gltfFile.Nodes[nodeNumber];
                    var parent = hierarchy[nodeNumber];
                    if (parent > -1 && lods[parent] > -1)
                        lods[nodeNumber] = lods[parent];
                    
                    matrices[nodeNumber] = GetMatrix(node);
                    matrixnames[nodeNumber] = $"node-{node.Name ?? string.Empty}";
                    if (node.Mesh != null) // FIXME: lod
                        meshes.Add(nodeNumber, node);
                    if (node.Children != null)
                    {
                        foreach (var child in node.Children)
                        {
                            hierarchy[child] = nodeNumber;
                            TempStack.Push(child);
                        }
                    }

                    object extension = null;
                    if (node?.Extensions?.TryGetValue("MSFT_lod", out extension) ?? false)
                    {
                        var ext = Newtonsoft.Json.JsonConvert.DeserializeObject<MSFT_lod>(extension.ToString());
                        var ids = ext?.Ids;
                        if (ids.Any())
                        {
                            lods[nodeNumber] = 0;
                            for (var j = 0; j < ids.Length; j++)
                            {
                                // The node defined in the MSFT_lod extension is a substitute of the current one, not an additional new step-to.
                                lods[ids[j]] = j + 1;
                                hierarchy[ids[j]] = parent;
                                TempStack.Push(ids[j]);
                                // TODO: MinimumScreenCoverage = MSFT_screencoverage[lodId];
                            }
                        }
                    }
                }
                Matrices = matrices;
                shape.Matrices = matrices;
                shape.MatrixNames = matrixnames.ToList();
                var subObjects = new List<SubObject>();
                foreach (var hierIndex in meshes.Keys)
                {
                    var node = meshes[hierIndex];
                    var mesh = gltfFile.Meshes[(int)node.Mesh];
                    var skin = node.Skin != null ? gltfFile.Skins[(int)node.Skin] : null;

                    for (var i = 0; i < mesh.Primitives.Length; i++)
                        subObjects.Add(new GltfSubObject(mesh.Primitives[i], $"{mesh.Name}[{i}]", hierIndex, hierarchy, Helpers.TextureFlags.None, gltfFile, shape, this, skin));
                }
                SubObjects = subObjects.ToArray();

                for (var j = 0; j < (gltfFile.Animations?.Length ?? 0); j++)
                {
                    var gltfAnimation = gltfFile.Animations[j];

                    GltfAnimation animation = new GltfAnimation();
                    for (var k = 0; k < gltfAnimation.Channels.Length; k++)
                    {
                        var gltfChannel = gltfAnimation.Channels[k];
                        if (gltfChannel.Target.Node == null) // then this is defined by an extension, which is not supported here.
                            continue;

                        var channel = new GltfAnimationChannel();
                        animation.Channels.Add(channel);

                        channel.Path = gltfChannel.Target.Path;
                        channel.TargetNode = (int)gltfChannel.Target.Node;

                        shape.MatrixNames[channel.TargetNode] = gltfAnimation.Name;
if (j == 0) shape.MatrixNames[(int)channel.TargetNode] = "ORTSITEM1CONTINUOUS";

                        var sampler = gltfAnimation.Samplers[gltfChannel.Sampler];
                        var inputAccessor = gltfFile.Accessors[sampler.Input];
                        channel.FrameCount = inputAccessor.Count;
                        channel.TimeArray = new float[inputAccessor.Count];
                        channel.TimeMin = inputAccessor.Min[0];
                        channel.TimeMax = inputAccessor.Max[0];
                        switch (channel.Path)
                        {
                            case AnimationChannelTarget.PathEnum.rotation: channel.OutputQuaternion = new Quaternion[inputAccessor.Count]; break;
                            case AnimationChannelTarget.PathEnum.scale:
                            case AnimationChannelTarget.PathEnum.translation: channel.OutputVector3 = new Vector3[inputAccessor.Count]; break;
                            case AnimationChannelTarget.PathEnum.weights: channel.OutputWeights = new float[inputAccessor.Count]; break;
                        }

                        var readInput = GetNormalizedReader(inputAccessor.ComponentType);
                        using (var br = new BinaryReader(GetBufferView(inputAccessor)))
                        {
                            for (var i = 0; i < inputAccessor.Count; i++)
                                channel.TimeArray[i] = readInput(br);
                        }

                        var outputAccessor = gltfFile.Accessors[sampler.Output];
                        var readOutput = GetNormalizedReader(outputAccessor.ComponentType);
                        using (var br = new BinaryReader(GetBufferView(outputAccessor)))
                        {
                            for (var i = 0; i < channel.FrameCount; i++)
                                switch (channel.Path)
                                {
                                    case AnimationChannelTarget.PathEnum.rotation: channel.OutputQuaternion[i] = new Quaternion(readOutput(br), readOutput(br), readOutput(br), readOutput(br)); break;
                                    case AnimationChannelTarget.PathEnum.scale:
                                    case AnimationChannelTarget.PathEnum.translation: channel.OutputVector3[i] = new Vector3(readOutput(br), readOutput(br), readOutput(br)); break;
                                    case AnimationChannelTarget.PathEnum.weights: channel.OutputWeights[i] = readOutput(br); morphWarning = true; break;
                                }
                        }
                    }
                    shape.GltfAnimations.Add(j, animation);
                }
                if (morphWarning)
                    Trace.TraceInformation($"glTF morphing animation is unsupported in file {gltfFileName}");
            }

            internal Stream GetBufferView(Accessor accessor)
            {
                var n = accessor.BufferView;
                if (n == null)
                    return Stream.Null;
                var bufferView = Gltf.BufferViews[(int)n];
                var file = $"{GltfDir}/{Gltf.Buffers[bufferView.Buffer].Uri}";
                if (!File.Exists(file))
                    return Stream.Null;
                var stream = File.OpenRead(file); // Need to be able to seek in the buffer
                stream.Seek(bufferView.ByteOffset + accessor.ByteOffset, SeekOrigin.Begin);
                return stream;
            }

            internal Texture2D GetTexture(Gltf gltf, int? textureIndex, Texture2D defaultTexture)
            {
                if (textureIndex != null)
                {
                    var texture = gltf.Textures[(int)textureIndex];
                    var source = texture?.Source;
                    var extensionFilter = StandardTextureExtensionFilter;
                    object extension = null;
                    if (texture?.Extensions?.TryGetValue("MSFT_texture_dds", out extension) ?? false)
                    {
                        var ext = Newtonsoft.Json.JsonConvert.DeserializeObject<MSFT_texture_dds>(extension.ToString());
                        source = ext?.Source ?? source;
                        extensionFilter = DdsTextureExtensionFilter;
                    }
                    var imagePath = source != null ? Path.Combine(Path.Combine(GltfDir, gltf.Images[(int)source].Uri)) : "";
                    if (File.Exists(imagePath))
                        using (var stream = File.OpenRead(imagePath))
                            return Viewer.TextureManager.Get(imagePath, defaultTexture, false, extensionFilter);
                }
                return defaultTexture;
            }

            internal Matrix GetMatrix(Node node)
            {
                var matrix = node.Matrix == null ? Matrix.Identity : new Matrix(
                    node.Matrix[0], node.Matrix[1], node.Matrix[2], node.Matrix[3],
                    node.Matrix[4], node.Matrix[5], node.Matrix[6], node.Matrix[7],
                    node.Matrix[8], node.Matrix[9], node.Matrix[10], node.Matrix[11],
                    node.Matrix[12], node.Matrix[13], node.Matrix[14], node.Matrix[15]);

                var scale = node.Scale == null ? Matrix.Identity : Matrix.CreateScale(node.Scale[0], node.Scale[1], node.Scale[2]);
                var rotation = node.Rotation == null ? Matrix.Identity : Matrix.CreateFromQuaternion(new Quaternion(node.Rotation[0], node.Rotation[1], node.Rotation[2], node.Rotation[3]));
                var transtaion = node.Translation == null ? Matrix.Identity : Matrix.CreateTranslation(node.Translation[0], node.Translation[1], node.Translation[2]);

                return matrix * scale * rotation * transtaion;
            }
        }

        public class GltfSubObject : SubObject
        {
            public GltfSubObject(MeshPrimitive meshPrimitive, string name, int hierarchyIndex, int[] hierarchy, Helpers.TextureFlags textureFlags, Gltf gltfFile, GltfShape shape, GltfDistanceLevel distanceLevel, Skin skin)
            {
                if (meshPrimitive.Indices == null)
                    return;
                if (meshPrimitive.Material == null)
                    return;

                var options = SceneryMaterialOptions.None;

                if (skin != null)
                {
                    options |= SceneryMaterialOptions.PbrHasSkin;
                    shape.SkeletonRootNode = skin.Skeleton ?? 0;
                }

                // This is according to the glTF spec:
                if (distanceLevel.Matrices[hierarchyIndex].Determinant() > 0)
                    options |= SceneryMaterialOptions.PbrCullClockWise;

                var material = gltfFile.Materials[(int)meshPrimitive.Material];
                switch (material.AlphaMode)
                {
                    case glTFLoader.Schema.Material.AlphaModeEnum.BLEND: options |= SceneryMaterialOptions.AlphaBlendingBlend; break;
                    case glTFLoader.Schema.Material.AlphaModeEnum.MASK: options |= SceneryMaterialOptions.AlphaTest; break;
                    case glTFLoader.Schema.Material.AlphaModeEnum.OPAQUE:
                    default: break;
                }
                options |= SceneryMaterialOptions.Diffuse;

                var referenceAlpha = material.AlphaCutoff; // default is 0.5
                var doubleSided = material.DoubleSided;

                var baseIndex = material.PbrMetallicRoughness?.BaseColorTexture?.Index;
                if (baseIndex != null)
                {
                    var samplerIndex = gltfFile.Textures[(int)baseIndex].Sampler;
                    var sampler = samplerIndex == null ? null : gltfFile.Samplers[(int)samplerIndex];
                    switch (sampler?.WrapS)
                    {
                        case Sampler.WrapSEnum.REPEAT: options |= SceneryMaterialOptions.TextureAddressModeWrap; break;
                        case Sampler.WrapSEnum.CLAMP_TO_EDGE: options |= SceneryMaterialOptions.TextureAddressModeClamp; break;
                        case Sampler.WrapSEnum.MIRRORED_REPEAT: options |= SceneryMaterialOptions.TextureAddressModeMirror; break;
                    }
                }

                // 8 bit sRGB + A. Needs decoding to linear in the shader.
                var baseColorTexCoord = material.PbrMetallicRoughness?.BaseColorTexture?.TexCoord ?? 0;
                var baseColorTexture = distanceLevel.GetTexture(gltfFile, material.PbrMetallicRoughness?.BaseColorTexture?.Index, SharedMaterialManager.WhiteTexture);
                var baseColorFactor = material.PbrMetallicRoughness?.BaseColorFactor ?? new[] { 1f, 1f, 1f, 1f };
                var baseColorFactorVector = new Vector4(baseColorFactor[0], baseColorFactor[1], baseColorFactor[2], baseColorFactor[3]);
                if (baseColorTexture != SharedMaterialManager.WhiteTexture) options |= SceneryMaterialOptions.PbrHasBaseColorMap;

                // G = roughness, B = metalness, linear, may be > 8 bit
                var metallicRoughnessTexCoord = material.PbrMetallicRoughness?.MetallicRoughnessTexture?.TexCoord ?? 1;
                var metallicRoughnessTexture = distanceLevel.GetTexture(gltfFile, material.PbrMetallicRoughness?.MetallicRoughnessTexture?.Index, SharedMaterialManager.WhiteTexture);
                var metallicFactor = material.PbrMetallicRoughness?.MetallicFactor ?? 1f;
                var roughtnessFactor = material.PbrMetallicRoughness?.RoughnessFactor ?? 1f;
                if (metallicRoughnessTexture != SharedMaterialManager.WhiteTexture) options |= SceneryMaterialOptions.PbrHasMetalRoughnessMap;

                // RGB linear, B should be >= 0.5. All channels need mapping from the [0.0..1.0] to the [-1.0..1.0] range, = sampledValue * 2.0 - 1.0
                var normalTexCoord = material.NormalTexture?.TexCoord ?? 0;
                var normalTexture = distanceLevel.GetTexture(gltfFile, material.NormalTexture?.Index, SharedMaterialManager.WhiteTexture);
                var normalScale = material.NormalTexture?.Scale ?? 1f;
                if (normalTexture != SharedMaterialManager.WhiteTexture) options |= SceneryMaterialOptions.PbrHasNormalMap;

                // R channel only, = 1.0 + strength * (sampledValue - 1.0)
                var occlusionTexCoord = material.OcclusionTexture?.TexCoord ?? 1;
                var occlusionTexture = distanceLevel.GetTexture(gltfFile, material.OcclusionTexture?.Index, SharedMaterialManager.WhiteTexture);
                var occlusionStrength = material.OcclusionTexture?.Strength ?? 1f;
                if (occlusionTexture != SharedMaterialManager.WhiteTexture) options |= SceneryMaterialOptions.PbrHasOcclusionMap;

                // 8 bit sRGB. Needs decoding to linear in the shader.
                var emissiveTexCoord = material.EmissiveTexture?.TexCoord ?? 0;
                var emissiveTexture = distanceLevel.GetTexture(gltfFile, material.EmissiveTexture?.Index, SharedMaterialManager.WhiteTexture);
                var emissiveFactor = material.EmissiveFactor ?? new[] { 0f, 0f, 0f };
                var emissiveFactorVector = new Vector3(emissiveFactor[0], emissiveFactor[1], emissiveFactor[2]);
                if (emissiveTexture != SharedMaterialManager.WhiteTexture) options |= SceneryMaterialOptions.PbrHasEmissiveMap;

                var indexBufferSet = new GltfIndexBufferSet();
                ushort[] indexData = null;

                if (meshPrimitive.Indices != null)
                {
                    var accessor = gltfFile.Accessors[(int)meshPrimitive.Indices];
                    indexData = new ushort[accessor.Count];
                    var read = GetIntegerReader(accessor.ComponentType);
                    using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                    {
                        for (var i = 0; i < indexData.Length; i++)
                            indexData[i] = read(br);
                    }
                    indexBufferSet.IndexBuffer = new IndexBuffer(shape.Viewer.GraphicsDevice, typeof(ushort), indexData.Length, BufferUsage.None);
                    indexBufferSet.IndexBuffer.SetData(indexData);
                    indexBufferSet.IndexBuffer.Name = name;

                    switch (meshPrimitive.Mode)
                    {
                        case MeshPrimitive.ModeEnum.TRIANGLE_STRIP: indexBufferSet.Mode = PrimitiveType.TriangleStrip; indexBufferSet.PrimitiveCount = indexData.Length - 2; break;
                        case MeshPrimitive.ModeEnum.TRIANGLES: indexBufferSet.Mode = PrimitiveType.TriangleList; indexBufferSet.PrimitiveCount = indexData.Length / 3; break;
                        case MeshPrimitive.ModeEnum.LINE_STRIP: indexBufferSet.Mode = PrimitiveType.LineStrip; indexBufferSet.PrimitiveCount = indexData.Length - 1; break;
                        case MeshPrimitive.ModeEnum.LINES: indexBufferSet.Mode = PrimitiveType.LineList; indexBufferSet.PrimitiveCount = indexData.Length / 2; break;
                        default: indexBufferSet.Mode = PrimitiveType.LineList; indexBufferSet.PrimitiveCount = indexData.Length / 2; break; // This is not exactly correct.
                    }
                }

                var vertexAttributes = new List<VertexBufferBinding>();

                VertexPosition[] vertexPositions = null;
                VertexNormal[] vertexNormals = null;
                VertexTextureDiffuse[] vertexTextureUvs = null;
                VertexBuffer vertexBufferTextureUvs = null;

                if (meshPrimitive.Attributes.TryGetValue("POSITION", out var accessorNumber))
                {
                    if (!shape.VertexBuffers.TryGetValue($"{name}/{accessorNumber}", out var vertexBufferBinding))
                    {
                        var accessor = gltfFile.Accessors[accessorNumber];
                        vertexPositions = new VertexPosition[accessor.Count];
                        using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                        {
                            for (var i = 0; i < vertexPositions.Length; i++)
                                vertexPositions[i] = new VertexPosition(new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
                        }
                        var vertexBuffer = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexPosition), vertexPositions.Length, BufferUsage.None);
                        vertexBuffer.SetData(vertexPositions);
                        vertexBuffer.Name = "POSITION";
                        vertexBufferBinding = new VertexBufferBinding(vertexBuffer);
                        shape.VertexBuffers.Add($"{name}/{accessorNumber}", vertexBufferBinding);
                    }
                    vertexAttributes.Add(vertexBufferBinding);
                }

                if (meshPrimitive.Attributes.TryGetValue("NORMAL", out accessorNumber))
                {
                    if (!shape.VertexBuffers.TryGetValue($"{name}/{accessorNumber}", out var vertexBufferBinding))
                    {
                        var accessor = gltfFile.Accessors[accessorNumber];
                        vertexNormals = new VertexNormal[accessor.Count];
                        using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                        {
                            for (var i = 0; i < vertexNormals.Length; i++)
                                vertexNormals[i] = new VertexNormal(new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
                        }
                        var vertexBuffer = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexNormal), vertexNormals.Length, BufferUsage.None);
                        vertexBuffer.SetData(vertexNormals);
                        vertexBuffer.Name = "NORMAL";
                        vertexBufferBinding = new VertexBufferBinding(vertexBuffer);
                        shape.VertexBuffers.Add($"{name}/{accessorNumber}", vertexBufferBinding);
                    }
                    vertexAttributes.Add(vertexBufferBinding);
                    options |= SceneryMaterialOptions.PbrHasNormals;
                }

                if (meshPrimitive.Attributes.TryGetValue("TEXCOORD_0", out accessorNumber))
                {
                    if (!shape.VertexBuffers.TryGetValue($"{name}/{accessorNumber}", out var vertexBufferBinding))
                    {
                        var accessor = gltfFile.Accessors[accessorNumber];
                        vertexTextureUvs = new VertexTextureDiffuse[accessor.Count];
                        var read = GetNormalizedReader(accessor.ComponentType);
                        using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                        {
                            for (var i = 0; i < vertexTextureUvs.Length; i++)
                                vertexTextureUvs[i] = new VertexTextureDiffuse(new Vector2(read(br), read(br)));
                        }
                        vertexBufferTextureUvs = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexTextureDiffuse), vertexTextureUvs.Length, BufferUsage.None);
                        vertexBufferTextureUvs.SetData(vertexTextureUvs);
                        vertexBufferTextureUvs.Name = "TEXCOORD_0";
                        vertexBufferBinding = new VertexBufferBinding(vertexBufferTextureUvs);
                        shape.VertexBuffers.Add($"{name}/{accessorNumber}", vertexBufferBinding);
                    }
                    vertexAttributes.Add(vertexBufferBinding);
                }
                else
                {
                    vertexTextureUvs = new VertexTextureDiffuse[vertexAttributes.First().VertexBuffer.VertexCount];
                    vertexTextureUvs.Initialize();
                    vertexBufferTextureUvs = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexTextureDiffuse), vertexTextureUvs.Length, BufferUsage.None);
                    vertexBufferTextureUvs.SetData(vertexTextureUvs);
                    vertexBufferTextureUvs.Name = "TEXCOORD_DUMMY";
                    var vertexBufferBinding = new VertexBufferBinding(vertexBufferTextureUvs);
                    vertexAttributes.Add(new VertexBufferBinding(vertexBufferTextureUvs));
                }

                if (meshPrimitive.Attributes.TryGetValue("TANGENT", out accessorNumber))
                {
                    if (!shape.VertexBuffers.TryGetValue($"{name}/{accessorNumber}", out var vertexBufferBinding))
                    {
                        var accessor = gltfFile.Accessors[accessorNumber];
                        var vertexData = new VertexTangent[accessor.Count];
                        using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                        {
                            for (var i = 0; i < vertexData.Length; i++)
                                vertexData[i] = new VertexTangent(new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
                        }
                        var vertexBuffer = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexTangent), vertexData.Length, BufferUsage.None);
                        vertexBuffer.SetData(vertexData);
                        vertexBuffer.Name = "TANGENT";
                        vertexBufferBinding = new VertexBufferBinding(vertexBuffer);
                        shape.VertexBuffers.Add($"{name}/{accessorNumber}", vertexBufferBinding);
                    }
                    vertexAttributes.Add(vertexBufferBinding);
                    options |= SceneryMaterialOptions.PbrHasTangents;
                }
                else if (indexData != null && vertexTextureUvs != null &&
                    (options & SceneryMaterialOptions.PbrHasNormals) != 0 && (options & SceneryMaterialOptions.PbrHasNormalMap) != 0 || (options & SceneryMaterialOptions.PbrHasSkin) != 0)
                {
                    var vertexData = CalculateTangents(indexData, vertexPositions, vertexNormals, vertexTextureUvs);
                    var vertexBuffer = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexTangent), vertexData.Length, BufferUsage.WriteOnly);
                    vertexBuffer.SetData(vertexData);
                    vertexBuffer.Name = "TANGENT_CALCULATED";
                    vertexAttributes.Add(new VertexBufferBinding(vertexBuffer));
                    options |= SceneryMaterialOptions.PbrHasTangents;
                }

                if (meshPrimitive.Attributes.TryGetValue("TEXCOORD_1", out accessorNumber))
                {
                    if (!shape.VertexBuffers.TryGetValue($"{name}/{accessorNumber}", out var vertexBufferBinding))
                    {
                        var accessor = gltfFile.Accessors[accessorNumber];
                        var vertexData = new VertexTextureMetallic[accessor.Count];
                        var read = GetNormalizedReader(accessor.ComponentType);
                        using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                        {
                            for (var i = 0; i < vertexData.Length; i++)
                                vertexData[i] = new VertexTextureMetallic(new Vector2(read(br), read(br)));
                        }
                        var vertexBuffer = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexTextureMetallic), vertexData.Length, BufferUsage.None);
                        vertexBuffer.SetData(vertexData);
                        vertexBuffer.Name = "TEXCOORD_1";
                        vertexBufferBinding = new VertexBufferBinding(vertexBuffer);
                        shape.VertexBuffers.Add($"{name}/{accessorNumber}", vertexBufferBinding);
                    }
                    vertexAttributes.Add(vertexBufferBinding);
                }
                else if ((options & SceneryMaterialOptions.PbrHasTangents) != 0 && vertexBufferTextureUvs != null)
                {
                    // In the shader where the tangents are defined also needs to be a secondary texture coordinate
                    vertexAttributes.Add(new VertexBufferBinding(vertexBufferTextureUvs));
                }

                if (meshPrimitive.Attributes.TryGetValue("JOINTS_0", out accessorNumber))
                {
                    if (!shape.VertexBuffers.TryGetValue($"{name}/{accessorNumber}", out var vertexBufferBinding))
                    {
                        var accessor = gltfFile.Accessors[accessorNumber];
                        var vertexData = new VertexJoint[accessor.Count];
                        var jointsRead = GetIntegerReader(accessor.ComponentType);
                        using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                        {
                            for (var i = 0; i < vertexData.Length; i++)
                                vertexData[i] = new VertexJoint(new Vector4(jointsRead(br), jointsRead(br), jointsRead(br), jointsRead(br)));
                        }
                        var vertexBuffer = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexJoint), vertexData.Length, BufferUsage.None);
                        vertexBuffer.SetData(vertexData);
                        vertexBuffer.Name = "JOINTS_0";
                        vertexBufferBinding = new VertexBufferBinding(vertexBuffer);
                        shape.VertexBuffers.Add($"{name}/{accessorNumber}", vertexBufferBinding);
                    }
                    vertexAttributes.Add(vertexBufferBinding);
                }

                if (meshPrimitive.Attributes.TryGetValue("WEIGHTS_0", out accessorNumber))
                {
                    if (!shape.VertexBuffers.TryGetValue($"{name}/{accessorNumber}", out var vertexBufferBinding))
                    {
                        var accessor = gltfFile.Accessors[accessorNumber];
                        var vertexData = new VertexWeight[accessor.Count];
                        var weightsRead = GetNormalizedReader(accessor.ComponentType);
                        using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                        {
                            for (var i = 0; i < vertexData.Length; i++)
                                vertexData[i] = new VertexWeight(new Vector4(weightsRead(br), weightsRead(br), weightsRead(br), weightsRead(br)));
                        }
                        var vertexBuffer = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexWeight), vertexData.Length, BufferUsage.None);
                        vertexBuffer.SetData(vertexData);
                        vertexBuffer.Name = "WEIGHTS_0";
                        vertexBufferBinding = new VertexBufferBinding(vertexBuffer);
                        shape.VertexBuffers.Add($"{name}/{accessorNumber}", vertexBufferBinding);
                    }
                    vertexAttributes.Add(vertexBufferBinding);
                }

                if (meshPrimitive.Attributes.TryGetValue("COLOR_0", out accessorNumber))
                {
                    if (!shape.VertexBuffers.TryGetValue($"{name}/{accessorNumber}", out var vertexBufferBinding))
                    {
                        var accessor = gltfFile.Accessors[accessorNumber];
                        var vertexData = new VertexColor4[accessor.Count];
                        var read = GetNormalizedReader(accessor.ComponentType);
                        if (accessor.Type == Accessor.TypeEnum.VEC3)
                        {
                            using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                            {
                                for (var i = 0; i < vertexData.Length; i++)
                                    vertexData[i] = new VertexColor4(new Vector4(read(br), read(br), read(br), 0));
                            }
                        }
                        else
                        {
                            using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                            {
                                for (var i = 0; i < vertexData.Length; i++)
                                    vertexData[i] = new VertexColor4(new Vector4(read(br), read(br), read(br), read(br)));
                            }
                        }
                        var vertexBuffer = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexColor4), vertexData.Length, BufferUsage.None);
                        vertexBuffer.SetData(vertexData);
                        vertexBuffer.Name = "COLOR_0";
                        vertexBufferBinding = new VertexBufferBinding(vertexBuffer);
                        shape.VertexBuffers.Add($"{name}/{accessorNumber}", vertexBufferBinding);
                    }
                    vertexAttributes.Add(vertexBufferBinding);
                }
                else if ((options & (SceneryMaterialOptions.PbrHasTangents | SceneryMaterialOptions.PbrHasSkin)) != 0)
                {
                    // In the shader where the tangents are defined also needs to be a COLOR_0
                    var vertexData = Enumerable.Repeat(new VertexColor4(Vector4.One), vertexAttributes.First().VertexBuffer.VertexCount).ToArray();
                    var vertexBuffer = new VertexBuffer(shape.Viewer.GraphicsDevice, typeof(VertexColor4), vertexData.Length, BufferUsage.None);
                    vertexBuffer.SetData(vertexData);
                    vertexBuffer.Name = "COLOR_DUMMY";
                    vertexAttributes.Add(new VertexBufferBinding(vertexBuffer));
                }

                // This is the dummy instance buffer at the end of the vertex buffers
                vertexAttributes.Add(new VertexBufferBinding(RenderPrimitive.GetDummyVertexBuffer(shape.Viewer.GraphicsDevice)));

                var sceneryMaterial = shape.Viewer.MaterialManager.Load("PBR", shape.FilePath + "#" + material.Name, (int)options, 0,
                    baseColorTexture, baseColorFactorVector,
                    metallicRoughnessTexture, metallicFactor, roughtnessFactor,
                    normalTexture, normalScale,
                    occlusionTexture, occlusionStrength,
                    emissiveTexture, emissiveFactorVector,
                    referenceAlpha, doubleSided);

                ShapePrimitives = new[] { new GltfPrimitive(sceneryMaterial, vertexAttributes, gltfFile, distanceLevel, indexBufferSet, skin, shape.Viewer.GraphicsDevice, hierarchyIndex, hierarchy) };
                ShapePrimitives[0].SortIndex = 0;
            }
        }
        
        public class GltfPrimitive : ShapePrimitive
        {
            public readonly int[] Joints; // Replaces ShapePrimitive.HierarchyIndex for non-skinned primitives
            public readonly Matrix[] InverseBindMatrices;
            private readonly VertexBufferBinding[] VertexBufferBinding;
            private readonly GltfIndexBufferSet IndexBufferSet;

            public GltfPrimitive(Material material, List<VertexBufferBinding> vertexAttributes, Gltf gltfFile, GltfDistanceLevel distanceLevel, GltfIndexBufferSet indexBufferSet, Skin skin, GraphicsDevice graphicsDevice, int hierarchyIndex, int[] hierarchy)
            {
                Material = material;
                Hierarchy = hierarchy;
                IndexBufferSet = indexBufferSet;
                PrimitiveCount = IndexBufferSet.PrimitiveCount;
                VertexBufferBinding = vertexAttributes.ToArray();
                HierarchyIndex = hierarchyIndex;

                if (skin == null)
                {
                    // Non-skinned model
                    Joints = new[] { HierarchyIndex };
                    InverseBindMatrices = new[] { Matrix.Identity };
                }
                else
                {
                    // Skinned model
                    Joints = skin.Joints;

                    if (!distanceLevel.AllInverseBindMatrices.TryGetValue((int)skin.InverseBindMatrices, out InverseBindMatrices))
                    {
                        if (skin.InverseBindMatrices == null)
                        {
                            InverseBindMatrices = Enumerable.Repeat(Matrix.Identity, Joints.Length).ToArray();
                        }
                        else
                        {
                            var accessor = gltfFile.Accessors[(int)skin.InverseBindMatrices];
                            InverseBindMatrices = new Matrix[accessor.Count];
                            var read = GetNormalizedReader(accessor.ComponentType);
                            using (var br = new BinaryReader(distanceLevel.GetBufferView(accessor)))
                            {
                                for (var i = 0; i < InverseBindMatrices.Length; i++)
                                    InverseBindMatrices[i] = new Matrix(
                                        read(br), read(br), read(br), read(br),
                                        read(br), read(br), read(br), read(br),
                                        read(br), read(br), read(br), read(br),
                                        read(br), read(br), read(br), read(br));
                            }
                        }
                        distanceLevel.AllInverseBindMatrices.Add((int)skin.InverseBindMatrices, InverseBindMatrices);
                    }
                }
            }
            
            public override void Draw(GraphicsDevice graphicsDevice)
            {
                if (PrimitiveCount > 0)
                {
                    graphicsDevice.SetVertexBuffers(VertexBufferBinding);
                    graphicsDevice.Indices = IndexBufferSet.IndexBuffer;
                    graphicsDevice.DrawIndexedPrimitives(IndexBufferSet.Mode, 0, 0, IndexBufferSet.PrimitiveCount);
                }
            }
        }
        
        public static VertexTangent[] CalculateTangents(ushort[] indices, VertexPosition[] vertexPosition, VertexNormal[] vertexNormal, VertexTextureDiffuse[] vertexTexture)
        {
            var vertexCount = vertexPosition.Length;

            var tan1 = new Vector3[vertexCount];
            var tan2 = new Vector3[vertexCount];

            for (var a = 0; a < indices.Length; a += 3)
            {
                var i1 = indices[a + 0];
                var i2 = indices[a + 1];
                var i3 = indices[a + 2];

                var v1 = vertexPosition[i1].Position;
                var v2 = vertexPosition[i2].Position;
                var v3 = vertexPosition[i3].Position;

                var w1 = vertexTexture[i1].TextureCoordinate;
                var w2 = vertexTexture[i2].TextureCoordinate;
                var w3 = vertexTexture[i3].TextureCoordinate;

                float x1 = v2.X - v1.X;
                float x2 = v3.X - v1.X;
                float y1 = v2.Y - v1.Y;
                float y2 = v3.Y - v1.Y;
                float z1 = v2.Z - v1.Z;
                float z2 = v3.Z - v1.Z;

                float s1 = w2.X - w1.X;
                float s2 = w3.X - w1.X;
                float t1 = w2.Y - w1.Y;
                float t2 = w3.Y - w1.Y;

                float div = s1 * t2 - s2 * t1;
                float r = div == 0.0f ? 0.0f : 1.0f / div; 

                Vector3 sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
                Vector3 tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);

                tan1[i1] += sdir;
                tan1[i2] += sdir;
                tan1[i3] += sdir;

                tan2[i1] += tdir;
                tan2[i2] += tdir;
                tan2[i3] += tdir;
            }

            var tangents = new VertexTangent[vertexCount];
            
            for (var a = 0; a < vertexCount; ++a)
            {
                Vector3 n = vertexNormal[a].Normal;
                Vector3 t = tan1[a];

                var tangentsW = Vector3.Dot(Vector3.Cross(n, t), tan2[a]) < 0.0f ? -1.0f : 1.0f;

                var tangent = Vector3.Normalize(t - n * Vector3.Dot(n, t));
                tangents[a] = new VertexTangent(new Vector4(tangent, tangentsW));
            }

            return tangents;
        }

        public struct GltfIndexBufferSet
        {
            public IndexBuffer IndexBuffer;
            public int PrimitiveCount;
            public PrimitiveType Mode;
        }
        
        public struct VertexPosition : IVertexType
        {
            Vector3 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0));
            public VertexPosition(Vector3 data) { vertexData = data; }
            public Vector3 Position { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 3;
        }
        
        public struct VertexNormal : IVertexType
        {
            Vector3 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0));
            public VertexNormal(Vector3 data) { vertexData = data; }
            public Vector3 Normal { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 3;
        }

        public struct VertexColor3 : IVertexType
        {
            Vector3 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Color, 0));
            public VertexColor3(Vector3 data) { vertexData = data; }
            public Vector3 Color { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 3;
        }

        public struct VertexColor4 : IVertexType
        {
            Vector4 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.Color, 0));
            public VertexColor4(Vector4 data) { vertexData = data; }
            public Vector4 Color { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 4;
        }

        public struct VertexJoint : IVertexType
        {
            Vector4 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.BlendIndices, 0));
            public VertexJoint(Vector4 data) { vertexData = data; }
            public Vector4 Joint { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 4;
        }

        public struct UIntVector4
        {
            public uint X;
            public uint Y;
            public uint Z;
            public uint W;

            public UIntVector4(uint x, uint y, uint z, uint w) => (X, Y, Z, W) = (x, y, z, w);
        }

        public struct VertexWeight : IVertexType
        {
            Vector4 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0));
            public VertexWeight(Vector4 data) { vertexData = data; }
            public Vector4 Weight { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 4;
        }

        public struct VertexTangent : IVertexType
        {
            Vector4 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.Tangent, 0));
            public VertexTangent(Vector4 data) { vertexData = data; }
            public Vector4 Tangent { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 4;
        }

        public struct VertexTextureDiffuse : IVertexType
        {
            Vector2 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));
            public VertexTextureDiffuse(Vector2 data) { vertexData = data; }
            public Vector2 TextureCoordinate { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 2;
        }

        public struct VertexTextureMetallic : IVertexType
        {
            Vector2 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 1));
            public VertexTextureMetallic(Vector2 data) { vertexData = data; }
            public Vector2 TextureCoordinate { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 2;
        }

        public struct VertexTextureNormalMap : IVertexType
        {
            Vector2 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 5));
            public VertexTextureNormalMap(Vector2 data) { vertexData = data; }
            public Vector2 TextureCoordinate { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 2;
        }

        public struct VertexTextureSpecularMap : IVertexType
        {
            Vector2 vertexData;
            public readonly static VertexDeclaration VertexDeclaration = new VertexDeclaration(SizeInBytes,
                new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 6));
            public VertexTextureSpecularMap(Vector2 data) { vertexData = data; }
            public Vector2 TextureCoordinate { get { return vertexData; } set { vertexData = value; } }
            VertexDeclaration IVertexType.VertexDeclaration { get { return VertexDeclaration; } }
            public const int SizeInBytes = sizeof(float) * 2;
        }
    }
}
