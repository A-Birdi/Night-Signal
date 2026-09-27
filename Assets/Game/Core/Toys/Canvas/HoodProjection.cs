using System;

namespace NightSignal.Core.Toys.Canvas
{
    /// <summary>
    /// Pure-math VIEW of the flat sheet on the optional display hood (Addendum 02 §6.1): a toy display object, not anybody's
    /// car. The hood is a crowned patch (parabolic crown across its width, gentle drop toward the nose). UVs are
    /// parameterized by ARC LENGTH in both directions, so ink keeps its proportions on the curved surface, and the sheet
    /// is fitted inside a margin with its aspect preserved. The mapping depends only on these constants, so the flat
    /// whiteboard and the hood always render the same committed document at the same places.
    /// </summary>
    public sealed class HoodProjection
    {
        public readonly double Width;
        public readonly double Length;
        public readonly double Crown;
        public readonly double NoseDrop;
        public readonly double Margin;

        const int Table = 256;
        readonly double[] xArc = new double[Table + 1];
        readonly double[] yArc = new double[Table + 1];
        readonly double uScale, vScale, uOffset, vOffset;

        public HoodProjection(double width = 1.40, double length = 1.10, double crown = 0.06, double noseDrop = 0.12, double margin = 0.04)
        {
            Width = width; Length = length; Crown = crown; NoseDrop = noseDrop; Margin = margin;
            for (int i = 1; i <= Table; i++)
            {
                double x0 = X(i - 1), x1 = X(i);
                xArc[i] = xArc[i - 1] + Math.Sqrt((x1 - x0) * (x1 - x0) + Math.Pow(CrownZ(x1) - CrownZ(x0), 2));
                double y0 = Y(i - 1), y1 = Y(i);
                yArc[i] = yArc[i - 1] + Math.Sqrt((y1 - y0) * (y1 - y0) + Math.Pow(DropZ(y1) - DropZ(y0), 2));
            }
            // Fit the 2:1 sheet into the usable UV rectangle preserving its aspect on the (arc-length) surface.
            double usableU = 1 - 2 * margin, usableV = 1 - 2 * margin;
            double surfaceAspect = (xArc[Table] * usableU) / (yArc[Table] * usableV);
            double sheetAspect = (double)CanvasLimits.SheetWidth / CanvasLimits.SheetHeight;
            if (sheetAspect > surfaceAspect) { uScale = usableU; vScale = usableV * surfaceAspect / sheetAspect; }
            else { vScale = usableV; uScale = usableU * sheetAspect / surfaceAspect; }
            uOffset = (1 - uScale) / 2;
            vOffset = (1 - vScale) / 2;
        }

        double X(int i) => -Width / 2 + Width * i / Table;
        double Y(int i) => Length * i / Table;
        double CrownZ(double x) { double t = 2 * x / Width; return Crown * (1 - t * t); }
        double DropZ(double y) { double t = y / Length; return -NoseDrop * t * t; }

        /// <summary>Sheet coordinates (x right, y down, 4096×2048) → hood UV in [0, 1]².</summary>
        public Vec2 SheetToUv(double sheetX, double sheetY) =>
            new Vec2(uOffset + uScale * sheetX / CanvasLimits.SheetWidth, vOffset + vScale * (1 - sheetY / CanvasLimits.SheetHeight));

        /// <summary>Inverse of <see cref="SheetToUv"/> (pointer picking on the hood view).</summary>
        public Vec2 UvToSheet(Vec2 uv) =>
            new Vec2((uv.X - uOffset) / uScale * CanvasLimits.SheetWidth, (1 - (uv.Y - vOffset) / vScale) * CanvasLimits.SheetHeight);

        /// <summary>UV → local hood-space point (x across, y toward the nose, z up), by arc-length lookup.</summary>
        public Vec3 UvToSurface(Vec2 uv)
        {
            double x = Invert(xArc, ToyMath.Clamp(uv.X, 0, 1) * xArc[Table], -Width / 2, Width);
            double y = Invert(yArc, ToyMath.Clamp(uv.Y, 0, 1) * yArc[Table], 0, Length);
            return new Vec3(x, y, CrownZ(x) + DropZ(y));
        }

        public Vec3 NormalAt(Vec2 uv)
        {
            Vec3 p = UvToSurface(uv);
            double dzdx = -8 * Crown * p.X / (Width * Width);
            double dzdy = -2 * NoseDrop * p.Y / (Length * Length);
            return new Vec3(-dzdx, -dzdy, 1).Normalized();
        }

        public Vec3 SheetToSurface(double sheetX, double sheetY) => UvToSurface(SheetToUv(sheetX, sheetY));

        static double Invert(double[] arc, double target, double start, double span)
        {
            int lo = 0, hi = Table;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (arc[mid] <= target) lo = mid; else hi = mid;
            }
            double seg = arc[hi] - arc[lo];
            double t = seg > 0 ? (target - arc[lo]) / seg : 0;
            return start + span * (lo + t) / Table;
        }

        /// <summary>A (cols+1)×(rows+1) grid mesh with stable UVs for the renderer (engine-free arrays).</summary>
        public void BuildGrid(int cols, int rows, out Vec3[] vertices, out Vec2[] uvs, out int[] triangles)
        {
            cols = Math.Max(1, cols); rows = Math.Max(1, rows);
            vertices = new Vec3[(cols + 1) * (rows + 1)];
            uvs = new Vec2[vertices.Length];
            for (int j = 0; j <= rows; j++)
                for (int i = 0; i <= cols; i++)
                {
                    var uv = new Vec2((double)i / cols, (double)j / rows);
                    int k = j * (cols + 1) + i;
                    uvs[k] = uv;
                    vertices[k] = UvToSurface(uv);
                }
            triangles = new int[cols * rows * 6];
            int t = 0;
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < cols; i++)
                {
                    int a = j * (cols + 1) + i, b = a + 1, c = a + cols + 1, d = c + 1;
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                }
        }
    }
}
