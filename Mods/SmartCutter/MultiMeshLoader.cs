using System.Collections.Generic;
using Assimp;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using AssimpMesh = Assimp.Mesh;
using AssimpVector3D = Assimp.Vector3D;
using UnityMatrix4x4 = UnityEngine.Matrix4x4;
using UnityMesh = UnityEngine.Mesh;

namespace SmartCutter
{
    /// <summary>
    /// Multi-mesh-aware FBX/OBJ loader. Shifter's
    /// <c>ShapezShifter.Kit.FileMeshLoader.LoadSingleMeshFromFile</c> calls
    /// <c>scene.Meshes.Single()</c> internally, which throws on any FBX exported with
    /// more than one mesh (very common — DCC tools split by material).
    ///
    /// <para>
    /// We can't reuse Shifter's <c>AssimpToUnityMeshConverter</c> either, because
    /// it was built against <c>AssimpNet 4.1.0.0</c> (signed, PublicKeyToken
    /// 0d51b391f59f42a6) while the AssimpNet shipped to Workshop is <c>4.1.1.0</c>
    /// (unsigned). MSBuild's reference resolver can't satisfy Shifter's strong-named
    /// dependency at compile time, so we inline the conversion against the 4.1.1.0
    /// API and combine all sub-meshes into one Unity <see cref="UnityMesh"/>.
    /// </para>
    ///
    /// Returns a single <see cref="UnityMesh"/> ready to wrap in
    /// <c>TemporaryMeshReference</c>.
    /// </summary>
    internal static class MultiMeshLoader
    {
        /// <summary>
        /// Orientation correction applied to every imported mesh. Adjust the
        /// Euler angles here if the FBX comes out tipped / yawed in-world.
        /// Currently a 90° pitch around the W↔E (X) axis — typical DCC tools
        /// export with Z-up while the game expects Y-up.
        /// </summary>
        private static readonly UnityMatrix4x4 OrientationFix =
            UnityMatrix4x4.Rotate(UnityEngine.Quaternion.Euler(-90f, 0f, 0f));

        /// <summary>
        /// N↔S mirror (flip along the Z axis, which is the N↔S axis after the
        /// orientation fix). Applied on top of <see cref="OrientationFix"/> when
        /// loading the mirror variant so the asymmetric body geometry flips with
        /// the wire-connector face. Vanilla buildings avoid this because their
        /// bodies are designed Y-symmetric; ours has an asymmetric wire-side cue.
        /// </summary>
        private static readonly UnityMatrix4x4 MirrorFix =
            UnityMatrix4x4.Scale(new UnityEngine.Vector3(1f, 1f, -1f)) * OrientationFix;

        public static UnityMesh LoadCombinedMeshFromFile(string file, bool mirror = false)
        {
            AssimpContext context = new AssimpContext();
            try
            {
                Scene scene = context.ImportFile(file, PostProcessPreset.TargetRealTimeMaximumQuality);

                UnityMatrix4x4 transformMatrix = mirror ? MirrorFix : OrientationFix;

                CombineInstance[] combine = new CombineInstance[scene.MeshCount];
                for (int i = 0; i < scene.MeshCount; i++)
                {
                    UnityMesh sub = ConvertAssimpMesh(scene.Meshes[i]);
                    combine[i] = new CombineInstance
                    {
                        mesh = sub,
                        transform = transformMatrix,
                        subMeshIndex = 0
                    };
                }

                string combinedName = scene.MeshCount > 0
                    ? scene.Meshes[0].Name + (mirror ? "_Mirrored" : "_Combined")
                    : (mirror ? "SmartCutter_Body_Mirrored" : "SmartCutter_Body_Combined");
                UnityMesh combined = new UnityMesh { name = combinedName };
                combined.CombineMeshes(combine, mergeSubMeshes: true, useMatrices: true);

                // CombineMeshes with a negative-determinant transform inverts the
                // triangle winding (back-faces become front-faces). Recalculate
                // normals so lighting matches the new winding.
                if (mirror)
                {
                    FlipTriangleWinding(combined);
                    combined.RecalculateNormals();
                    combined.RecalculateBounds();
                }

                for (int i = 0; i < combine.Length; i++)
                {
                    if (combine[i].mesh != null)
                    {
                        Object.Destroy(combine[i].mesh);
                    }
                }

                return combined;
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>
        /// Reverse triangle winding on every submesh in place. CombineMeshes applied
        /// to a transform with negative determinant (our N↔S mirror) inverts winding,
        /// turning front-faces into back-faces. Swapping the 2nd and 3rd index of each
        /// triangle restores correct front-face orientation before
        /// <c>RecalculateNormals</c> is called.
        /// </summary>
        private static void FlipTriangleWinding(UnityMesh mesh)
        {
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                int[] tris = mesh.GetTriangles(sub);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int swap = tris[i + 1];
                    tris[i + 1] = tris[i + 2];
                    tris[i + 2] = swap;
                }
                mesh.SetTriangles(tris, sub);
            }
        }

        /// <summary>
        /// Convert a single <see cref="AssimpMesh"/> to a Unity <see cref="UnityMesh"/>.
        /// Mirrors the logic in Shifter's <c>AssimpToUnityMeshConverter</c> but built
        /// against the AssimpNet 4.1.1.0 API surface so MSBuild's reference resolver
        /// doesn't need to find Shifter's strong-named 4.1.0.0 dependency.
        /// </summary>
        private static UnityMesh ConvertAssimpMesh(AssimpMesh source)
        {
            UnityMesh target = new UnityMesh { name = source.Name };

            int vertexCount = source.Vertices.Count;
            NativeArray<float3> vertices = new NativeArray<float3>(vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < vertexCount; i++)
            {
                AssimpVector3D v = source.Vertices[i];
                vertices[i] = new float3(-v.X, v.Y, v.Z);
            }
            target.SetVertices(vertices);
            vertices.Dispose();

            List<int> indices = new List<int>(source.FaceCount * 3);
            for (int i = 0; i < source.FaceCount; i++)
            {
                Face face = source.Faces[i];
                if (face.IndexCount == 3)
                {
                    // Reverse winding to flip with the mirrored X axis above.
                    indices.Add(face.Indices[2]);
                    indices.Add(face.Indices[1]);
                    indices.Add(face.Indices[0]);
                }
            }
            target.SetIndices(indices.ToArray(), MeshTopology.Triangles, 0, calculateBounds: true);

            int normalCount = source.Normals.Count;
            if (normalCount > 0)
            {
                NativeArray<float3> normals = new NativeArray<float3>(normalCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < normalCount; i++)
                {
                    AssimpVector3D n = source.Normals[i];
                    normals[i] = new float3(-n.X, n.Y, n.Z);
                }
                target.SetNormals(normals);
                normals.Dispose();
            }

            for (int channel = 0; channel < source.TextureCoordinateChannels.Length; channel++)
            {
                if (!source.HasTextureCoords(channel)) continue;
                List<AssimpVector3D> uvChannel = source.TextureCoordinateChannels[channel];
                NativeArray<float2> uvs = new NativeArray<float2>(uvChannel.Count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int j = 0; j < uvChannel.Count; j++)
                {
                    AssimpVector3D uv = uvChannel[j];
                    uvs[j] = new float2(uv.X, uv.Y);
                }
                target.SetUVs(channel, uvs);
                uvs.Dispose();
            }

            return target;
        }
    }
}
