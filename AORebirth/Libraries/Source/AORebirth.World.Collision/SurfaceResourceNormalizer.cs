namespace AORebirth.World.Collision
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Numerics;

    using AODB.Common.RDBObjects;

    using AodbVector3 = AODB.Common.Structs.Vector3;

    /// <summary>Flattens AODB <see cref="SurfaceResource"/> meshes into engine-agnostic triangle meshes.</summary>
    internal static class SurfaceResourceNormalizer
    {
        public static void AppendMeshes(
            SurfaceResource? surface,
            int? cellId,
            List<CollisionTriangleMesh> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (surface?.Surfaces == null || surface.Surfaces.Count == 0)
                return;

            for (int i = 0; i < surface.Surfaces.Count; i++)
            {
                SurfaceMesh? mesh = surface.Surfaces[i];
                if (mesh?.Vertices == null || mesh.Triangles == null
                    || mesh.Vertices.Count < 3 || mesh.Triangles.Count < 1)
                    continue;

                if (TryNormalize(mesh.Vertices, mesh.Triangles, cellId, out CollisionTriangleMesh? normalized)
                    && normalized != null)
                {
                    destination.Add(normalized);
                }
            }
        }

        /// <summary>
        /// AODB stores vertices as <see cref="AodbVector3"/> and triangles as <see cref="Int3"/>; those are read
        /// directly. Anything else goes through reflection (boxing every value), which a playfield build cannot
        /// afford for its hundreds of thousands of vertices.
        /// </summary>
        static bool TryNormalize(
            IList vertices,
            IList triangles,
            int? cellId,
            out CollisionTriangleMesh? mesh)
        {
            mesh = null;
            int vertexCount = vertices.Count;
            int triangleCount = triangles.Count;
            var verts = new Vector3[vertexCount];
            if (vertices is List<AodbVector3> typedVertices)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    AodbVector3 v = typedVertices[i];
                    verts[i] = new Vector3(v.X, v.Y, v.Z);
                }
            }
            else
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    object? vertex = vertices[i];
                    if (vertex == null)
                        return false;
                    verts[i] = ReadVec(vertex);
                }
            }

            var tris = new CollisionTriangle[triangleCount];
            int valid = 0;
            if (triangles is List<Int3> typedTriangles)
            {
                for (int t = 0; t < triangleCount; t++)
                {
                    Int3 tri = typedTriangles[t];
                    if (IsValid(tri.A, tri.B, tri.C, vertexCount))
                        tris[valid++] = new CollisionTriangle(tri.A, tri.B, tri.C);
                }
            }
            else
            {
                for (int t = 0; t < triangleCount; t++)
                {
                    object? triObj = triangles[t];
                    if (triObj == null)
                        continue;

                    int aIdx = ReadIndex(triObj, "A");
                    int bIdx = ReadIndex(triObj, "B");
                    int cIdx = ReadIndex(triObj, "C");
                    if (IsValid(aIdx, bIdx, cIdx, vertexCount))
                        tris[valid++] = new CollisionTriangle(aIdx, bIdx, cIdx);
                }
            }

            if (valid == 0)
                return false;
            if (valid < tris.Length)
                Array.Resize(ref tris, valid);

            mesh = new CollisionTriangleMesh(verts, tris, cellId);
            return true;
        }

        static bool IsValid(int a, int b, int c, int vertexCount)
            => (uint)a < (uint)vertexCount && (uint)b < (uint)vertexCount && (uint)c < (uint)vertexCount;

        static int ReadIndex(object tri, string name)
        {
            object? v = tri.GetType().GetProperty(name)?.GetValue(tri)
                ?? tri.GetType().GetField(name)?.GetValue(tri);
            return v switch
            {
                int i => i,
                short s => s,
                _ => -1
            };
        }

        static Vector3 ReadVec(object v)
        {
            if (v is Vector3 numerics)
                return numerics;
            if (v is AodbVector3 aodb)
                return new Vector3(aodb.X, aodb.Y, aodb.Z);

            Type t = v.GetType();
            float x = Convert.ToSingle(t.GetProperty("X")?.GetValue(v) ?? t.GetField("X")?.GetValue(v) ?? 0);
            float y = Convert.ToSingle(t.GetProperty("Y")?.GetValue(v) ?? t.GetField("Y")?.GetValue(v) ?? 0);
            float z = Convert.ToSingle(t.GetProperty("Z")?.GetValue(v) ?? t.GetField("Z")?.GetValue(v) ?? 0);
            return new Vector3(x, y, z);
        }
    }
}
