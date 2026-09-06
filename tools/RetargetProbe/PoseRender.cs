using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using GFDLibrary.Models;

static class PoseRender
{
    public static void Draw(Model source, Dictionary<Node, Matrix4x4> sourcePose, Model target, Dictionary<Node, Matrix4x4> targetPose, string path, string caption)
    {
        using var bitmap = new Bitmap(1200, 700);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(38, 42, 46));
        using var font = new Font("Arial", 14);
        g.DrawString(caption, font, Brushes.White, 20, 15);
        DrawModel(g, source, sourcePose, 300);
        DrawModel(g, target, targetPose, 900);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static void DrawModel(Graphics g, Model model, Dictionary<Node, Matrix4x4> pose, float center)
    {
        var nodes = model.Nodes.ToArray();
        var triangles = new List<(Vector3 a, Vector3 b, Vector3 c, Color color)>();
        foreach (var node in nodes) foreach (var mesh in node.Meshes) {
            if ((mesh.MaterialName ?? "").Contains("outline", StringComparison.OrdinalIgnoreCase)) continue;
            var vertices = new Vector3[mesh.VertexCount];
            for (int i = 0; i < vertices.Length; i++) {
                if (mesh.VertexWeights == null) vertices[i] = Vector3.Transform(mesh.Vertices[i], pose[node]);
                else {
                    var weights = mesh.VertexWeights[i];
                    for (int j = 0; j < weights.Weights.Length; j++) if (weights.Weights[j] != 0) {
                        var bone = model.Bones[weights.Indices[j]];
                        vertices[i] += Vector3.Transform(mesh.Vertices[i], bone.InverseBindMatrix * pose[nodes[bone.NodeIndex]]) * weights.Weights[j];
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
        // Follow the character, so root-motion clips remain visible. This is a
        // pose comparison, not an assertion that root trajectories coincide.
        var focusNode = nodes.FirstOrDefault(n => n.Name == "Bip01") ?? nodes.First(n => n.Name == "root");
        var focus = pose[focusNode].Translation - new Vector3(0, 95, 0);
        PointF Project(Vector3 world) {
            var p = world - focus;
            return new(center + (p.X * .94f - p.Z * .34f) * 2.9f, 630 - (p.Y + p.X * .07f + p.Z * .18f) * 2.9f);
        }
        float Depth(Vector3 p) => p.Z * .94f + p.X * .34f;
        g.DrawLine(Pens.Gray, center - 280, 630, center + 280, 630);
        foreach (var t in triangles.OrderBy(t => Depth((t.a+t.b+t.c)/3))) {
            if (!float.IsFinite(t.a.X+t.a.Y+t.a.Z+t.b.X+t.b.Y+t.b.Z+t.c.X+t.c.Y+t.c.Z)) throw new Exception("Nonfinite skin vertex");
            using var brush = new SolidBrush(t.color);
            g.FillPolygon(brush, new[] {Project(t.a),Project(t.b),Project(t.c)});
        }
    }
}
