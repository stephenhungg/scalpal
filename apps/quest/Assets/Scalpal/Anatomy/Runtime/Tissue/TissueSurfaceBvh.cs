using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // Rest-topology BVH, refitted to the exact current display proxy. Unlike a rest SDF,
    // queries need no approximate inverse of the trilinear deformation. This still uses
    // the nearest outward normal sign; it is not an exact winding SDF or CCD solver.
    public sealed class TissueSurfaceBvh : IDisposable
    {
        struct Node { public float3 min, max; public int left, right, start, count, escape; }
        public struct Hit
        {
            public int triangle, testedTriangles;
            public float penetration;
            public float3 normal, barycentric;
        }
        NativeArray<Node> nodes;
        NativeArray<float3> vertices, points;
        NativeArray<int> indices, order, stack;
        NativeArray<Hit> hits;
        public int TriangleCount => indices.IsCreated ? indices.Length / 3 : 0;
        public int LastTriangleTests { get; private set; }
        public bool IsCreated => nodes.IsCreated;

        public TissueSurfaceBvh(Vector3[] rest, int[] triangles, int maximumQueries = 128)
        {
            if (rest == null || triangles == null || triangles.Length == 0 || triangles.Length % 3 != 0 || maximumQueries <= 0)
                throw new ArgumentException("BVH requires triangles and a positive query budget.");
            var permutation = new int[triangles.Length / 3];
            for (int i = 0; i < permutation.Length; i++) permutation[i] = i;
            var tree = new List<Node>(permutation.Length * 2);
            Build(tree, permutation, 0, permutation.Length, rest, triangles);
            nodes = new NativeArray<Node>(tree.ToArray(), Allocator.Persistent);
            vertices = new NativeArray<float3>(rest.Length, Allocator.Persistent);
            indices = new NativeArray<int>(triangles, Allocator.Persistent);
            order = new NativeArray<int>(permutation, Allocator.Persistent);
            stack = new NativeArray<int>(64, Allocator.Persistent);
            points = new NativeArray<float3>(maximumQueries, Allocator.Persistent);
            hits = new NativeArray<Hit>(maximumQueries, Allocator.Persistent);
            Refit(rest);
        }
        static int Build(List<Node> tree, int[] permutation, int start, int count, Vector3[] rest, int[] triangles)
        {
            int at = tree.Count; tree.Add(default);
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            Vector3 Center(int t) => (rest[triangles[t * 3]] + rest[triangles[t * 3 + 1]] + rest[triangles[t * 3 + 2]]) / 3;
            for (int i = start; i < start + count; i++) { var c = Center(permutation[i]); min = Vector3.Min(min, c); max = Vector3.Max(max, c); }
            var node = new Node { start = start, count = count, left = -1, right = -1 };
            if (count > 8)
            {
                var extent = max - min; int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
                Array.Sort(permutation, start, count, Comparer<int>.Create((a, b) => Center(a)[axis].CompareTo(Center(b)[axis])));
                int half = count / 2;
                node.count = 0;
                node.left = Build(tree, permutation, start, half, rest, triangles);
                node.right = Build(tree, permutation, start + half, count - half, rest, triangles);
            }
            node.escape = tree.Count; tree[at] = node; return at;
        }
        public void Refit(Vector3[] deformedWorld)
        {
            if (!IsCreated || deformedWorld.Length != vertices.Length) throw new ArgumentException("BVH vertex count changed.");
            for (int i = 0; i < vertices.Length; i++) vertices[i] = deformedWorld[i];
            new RefitJob { nodes = nodes, vertices = vertices, indices = indices, order = order }.Run();
        }
        public void Query(Vector3[] worldPoints, int count)
        {
            if (!IsCreated || count < 0 || count > points.Length || worldPoints.Length < count) throw new ArgumentException("BVH query budget exceeded.");
            for (int i = 0; i < count; i++) points[i] = worldPoints[i];
            new QueryJob { nodes = nodes, vertices = vertices, indices = indices, order = order, points = points, hits = hits, count = count, stack = stack }.Run();
            LastTriangleTests = 0;
            for (int i = 0; i < count; i++) LastTriangleTests += hits[i].testedTriangles;
        }
        public Hit Result(int query) => hits[query];
        public void Dispose()
        {
            if (nodes.IsCreated) nodes.Dispose(); if (vertices.IsCreated) vertices.Dispose();
            if (indices.IsCreated) indices.Dispose(); if (order.IsCreated) order.Dispose();
            if (stack.IsCreated) stack.Dispose(); if (points.IsCreated) points.Dispose(); if (hits.IsCreated) hits.Dispose();
        }
        [BurstCompile(CompileSynchronously = true)]
        struct RefitJob : IJob
        {
            public NativeArray<Node> nodes;
            [ReadOnly] public NativeArray<float3> vertices;
            [ReadOnly] public NativeArray<int> indices, order;
            public void Execute()
            {
                for (int i = nodes.Length - 1; i >= 0; i--)
                {
                    var node = nodes[i]; node.min = new float3(float.PositiveInfinity); node.max = new float3(float.NegativeInfinity);
                    if (node.count > 0)
                        for (int j = node.start; j < node.start + node.count; j++)
                            for (int k = 0; k < 3; k++) { var v = vertices[indices[order[j] * 3 + k]]; node.min = math.min(node.min, v); node.max = math.max(node.max, v); }
                    else { node.min = math.min(nodes[node.left].min, nodes[node.right].min); node.max = math.max(nodes[node.left].max, nodes[node.right].max); }
                    nodes[i] = node;
                }
            }
        }
        [BurstCompile(CompileSynchronously = true)]
        struct QueryJob : IJob
        {
            [ReadOnly] public NativeArray<Node> nodes;
            [ReadOnly] public NativeArray<float3> vertices, points;
            [ReadOnly] public NativeArray<int> indices, order;
            public NativeArray<Hit> hits;
            public NativeArray<int> stack;
            public int count;
            public void Execute()
            {
                for (int query = 0; query < count; query++)
                {
                    float3 point = points[query]; float nearest = float.PositiveInfinity, signed = 0;
                    var hit = new Hit { triangle = -1 };
                    int pending = 1; stack[0] = 0;
                    while (pending > 0)
                    {
                        var node = nodes[stack[--pending]];
                        if (Distance2(point,node) > nearest + 1e-12f) continue;
                        if (node.count == 0)
                        {
                            int near=node.left,far=node.right;
                            if(Distance2(point,nodes[near])>Distance2(point,nodes[far])){int swap=near;near=far;far=swap;}
                            stack[pending++]=far;stack[pending++]=near;continue;
                        }
                        for (int j = node.start; j < node.start + node.count; j++)
                        {
                            int t = order[j]; hit.testedTriangles++;
                            float3 a = vertices[indices[t * 3]], b = vertices[indices[t * 3 + 1]], c = vertices[indices[t * 3 + 2]];
                            float3 area = math.cross(b - a, c - a); float length2 = math.lengthsq(area);
                            if (length2 <= 1e-20f) continue;
                            float3 normal = area * math.rsqrt(length2), closest = Closest(point, a, b, c);
                            float distance = math.lengthsq(point - closest), dot = math.dot(point - closest, normal);
                            if (distance > nearest + 1e-12f || (math.abs(distance - nearest) < 1e-12f && (dot < signed || (dot == signed && t > hit.triangle)))) continue;
                            float3 v0 = b - a, v1 = c - a, v2 = closest - a;
                            float d00 = math.dot(v0, v0), d01 = math.dot(v0, v1), d11 = math.dot(v1, v1), d20 = math.dot(v2, v0), d21 = math.dot(v2, v1);
                            float denominator = d00 * d11 - d01 * d01;
                            if (math.abs(denominator) < 1e-20f) continue;
                            float u = (d11 * d20 - d01 * d21) / denominator, v = (d00 * d21 - d01 * d20) / denominator;
                            nearest = distance; signed = dot; hit.triangle = t; hit.normal = normal; hit.barycentric = new float3(1 - u - v, u, v);
                        }
                    }
                    hit.penetration = hit.triangle < 0 ? 0 : (signed < 0 ? 1 : -1) * math.sqrt(nearest);
                    hits[query] = hit;
                }
            }
            static float Distance2(float3 point,Node node) => math.lengthsq(point-math.clamp(point,node.min,node.max));
            static float3 Closest(float3 p, float3 a, float3 b, float3 c)
            {
                float3 ab = b - a, ac = c - a, ap = p - a;
                float d1 = math.dot(ab, ap), d2 = math.dot(ac, ap);
                if (d1 <= 0 && d2 <= 0) return a;
                float3 bp = p - b; float d3 = math.dot(ab, bp), d4 = math.dot(ac, bp);
                if (d3 >= 0 && d4 <= d3) return b;
                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
                float3 cp = p - c; float d5 = math.dot(ab, cp), d6 = math.dot(ac, cp);
                if (d6 >= 0 && d5 <= d6) return c;
                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
                float va = d3 * d6 - d5 * d4;
                if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
                float inverse = 1 / (va + vb + vc); return a + ab * (vb * inverse) + ac * (vc * inverse);
            }
        }
    }
}
