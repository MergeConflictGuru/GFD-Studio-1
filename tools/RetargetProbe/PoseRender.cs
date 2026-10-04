using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Numerics;
using GFDLibrary.Animations;
using GFDLibrary.Models;

static class PoseRender
{
    public static void Draw(Model source, Dictionary<Node, Matrix4x4> sourcePose, Model target, Dictionary<Node, Matrix4x4> targetPose, string path, string caption, Model[]? parts = null, bool centerEachPose = false)
    {
        using var bitmap = new Bitmap(1200, 700);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(38, 42, 46));
        using var font = new Font("Arial", 14);
        g.DrawString(caption, font, Brushes.White, 20, 15);
        var sourceFocus = GetFocusPosition(source, sourcePose);
        var targetFocus = GetFocusPosition(target, targetPose);
        var focus = (sourceFocus + targetFocus) * .5f - new Vector3(0, 95, 0);
        DrawModel(g, source, sourcePose, 300, centerEachPose ? new Vector3(sourceFocus.X, 0, sourceFocus.Z) : focus, parts);
        DrawModel(g, target, targetPose, 900, centerEachPose ? new Vector3(targetFocus.X, 0, targetFocus.Z) : focus, parts);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static Vector3 GetFocusPosition(Model model, Dictionary<Node, Matrix4x4> pose)
    {
        var nodes = model.Nodes.ToArray();
        var focusNode = AnimationSkeletonRoles.ResolveMotionRoot(model) ??
                        nodes.FirstOrDefault(n => n.Name == "head" || n.Name == "neck") ??
                        nodes[0];
        return pose[focusNode].Translation;
    }

    private static void DrawModel(Graphics g, Model model, Dictionary<Node, Matrix4x4> pose, float center, Vector3 focus, Model[]? parts)
    {
        var models = new List<(Model model, Dictionary<Node, Matrix4x4> pose)> {(model,pose)};
        var named = pose.GroupBy(p=>p.Key.Name).ToDictionary(g=>g.Key,g=>g.First().Value);
        foreach(var part in parts ?? []) {
            var partPose = new Dictionary<Node,Matrix4x4>();
            Matrix4x4 World(Node node) {
                if(partPose.TryGetValue(node,out var matrix))return matrix;
                matrix=named.TryGetValue(node.Name,out var shared)?shared:node.LocalTransform*(node.Parent is null?Matrix4x4.Identity:World(node.Parent));
                partPose[node]=matrix;return matrix;
            }
            foreach(var node in part.Nodes) World(node);
            models.Add((part,partPose));
        }
        var triangles = new List<(Vector3 a, Vector3 b, Vector3 c, Color color)>();
        foreach(var (part,partPose) in models) {
        var nodes=part.Nodes.ToArray();
        foreach (var node in nodes) foreach (var mesh in node.Meshes) {
            if ((mesh.MaterialName ?? "").Contains("outline", StringComparison.OrdinalIgnoreCase)) continue;
            var vertices = new Vector3[mesh.VertexCount];
            for (int i = 0; i < vertices.Length; i++) {
                if (mesh.VertexWeights == null) vertices[i] = Vector3.Transform(mesh.Vertices[i], partPose[node]);
                else {
                    var weights = mesh.VertexWeights[i];
                    for (int j = 0; j < weights.Weights.Length; j++) if (weights.Weights[j] != 0) {
                        var bone = part.Bones[weights.Indices[j]];
                        vertices[i] += Vector3.Transform(mesh.Vertices[i], bone.InverseBindMatrix * partPose[nodes[bone.NodeIndex]]) * weights.Weights[j];
                    }
                }
            }
            foreach (var t in mesh.Triangles) {
                var a = vertices[t.A]; var b = vertices[t.B]; var c = vertices[t.C];
                var normal = Vector3.Normalize(Vector3.Cross(b-a,c-a));
                var shade = (int)(100 + 130 * Math.Abs(Vector3.Dot(normal, Vector3.Normalize(new Vector3(.3f,.5f,1)))));
                triangles.Add((a,b,c,Color.FromArgb(Math.Clamp(shade,0,255),Math.Clamp(shade,0,255),Math.Clamp(shade,0,255))));
            }
        }
        }
        var angle=int.TryParse(Environment.GetEnvironmentVariable("GFD_REVIEW_YAW"),out var degrees)?degrees:0;
        var camera=Matrix4x4.CreateRotationY(angle*MathF.PI/180);
        Vector3 Camera(Vector3 world)=>Vector3.TransformNormal(world-focus,camera);
        PointF Project(Vector3 world) {
            var p = Camera(world);
            return new(center + (p.X * .94f - p.Z * .34f) * 2.9f, 630 - (p.Y + p.X * .07f + p.Z * .18f) * 2.9f);
        }
        float Depth(Vector3 world) {var p=Camera(world);return p.Z * .94f + p.X * .34f;}
        g.DrawLine(Pens.Gray, center - 280, 630, center + 280, 630);
        foreach (var t in triangles.OrderBy(t => Depth((t.a+t.b+t.c)/3))) {
            if (!float.IsFinite(t.a.X+t.a.Y+t.a.Z+t.b.X+t.b.Y+t.b.Z+t.c.X+t.c.Y+t.c.Z)) throw new Exception("Nonfinite skin vertex");
            using var brush = new SolidBrush(t.color);
            g.FillPolygon(brush, new[] {Project(t.a),Project(t.b),Project(t.c)});
        }
    }
}
