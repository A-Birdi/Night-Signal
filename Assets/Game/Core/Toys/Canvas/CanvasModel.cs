using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NightSignal.Core.Toys.Canvas
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum CanvasObjectKind { Stroke = 0, Line = 1, Rect = 2, Ellipse = 3, Text = 4, Stamp = 5 }

    /// <summary>
    /// Budgets of the compact vector document (Addendum 02 §6.2). Initial engineering budgets that are measured by tests;
    /// they bound malicious payloads and never silently erase accepted strokes (an over-budget operation is rejected).
    /// </summary>
    public static class CanvasLimits
    {
        public const int SheetWidth = 4096;
        public const int SheetHeight = 2048;
        public const int MaxSheets = 6;
        public const int MinRetainedSheets = 3;
        public const int MaxObjects = 4096;
        public const int MaxPointsPerOp = 128;
        public const int MaxPointsPerStroke = 1024;
        public const int MaxTotalPoints = 131_072;
        public const int MaxTextLength = 80;
        public const int MinWidth = 1;
        public const int MaxWidth = 48;
        public const int MinTextSize = 16;
        public const int MaxTextSize = 256;
        public const int UndoDepth = 64;
        public const long TransformLeaseMs = 4_000;
        public const int MaxCheckpoints = 3;
        /// <summary>Compressed current document (sheets + checkpoints).</summary>
        public const int MaxCompressedBytes = 1024 * 1024;
        /// <summary>Decompression bound for stored/received documents.</summary>
        public const int MaxInflatedBytes = 12 * 1024 * 1024;
    }

    /// <summary>One mark on a sheet, with a stable id and its author. Text is stored and returned as plain data only.</summary>
    public sealed class CanvasObject
    {
        public string Id;
        public string Author;
        public CanvasObjectKind Kind;
        public long Color;
        public int Width;
        /// <summary>Flat quantized sheet coordinates x0,y0,x1,y1,… (strokes, lines and shape corners; text/stamp anchor).</summary>
        public int[] Points;
        public string Text;
        public string StampId;
        public int Size;
        public int Tx;
        public int Ty;
        /// <summary>Rotation in 1/100 degree about the object's anchor.</summary>
        public int Rot;
        /// <summary>Scale in 1/1000 about the object's anchor.</summary>
        public int Scale = 1000;
        /// <summary>Owner allows friends to edit (under a short transform lease).</summary>
        public bool Shared;
        /// <summary>A freehand stroke still being drawn.</summary>
        public bool Open;
        /// <summary>Tombstone kept only while an undo/redo entry can still restore it.</summary>
        public bool Deleted;
        public long Revision;
        public string LeaseHolder;
        public long LeaseUntilMs;

        [JsonIgnore] public int PointCount => Points == null ? 0 : Points.Length / 2;
    }

    public sealed class CanvasSheet
    {
        public string SheetId;
        public string Name;
        /// <summary>Bumped by a whole-sheet clear: operations addressed to an older epoch are rejected.</summary>
        public int Epoch = 1;
        public List<CanvasObject> Objects = new List<CanvasObject>();
    }

    /// <summary>A recoverable copy of a sheet (always taken before a whole-sheet clear).</summary>
    public sealed class CanvasCheckpoint
    {
        public string CheckpointId;
        public string SheetId;
        public string SheetName;
        public string Reason;
        public long AtRevision;
        public List<CanvasObject> Objects = new List<CanvasObject>();
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum CanvasUndoKind { Presence = 0, Transform = 1, Style = 2, Share = 3 }

    /// <summary>The state of ONE object before an own action; applying it swaps state (undo ↔ redo).</summary>
    public sealed class CanvasUndoEntry
    {
        public CanvasUndoKind Kind;
        public string ObjectId;
        public string SheetId;
        public int SheetEpoch;
        /// <summary>The object's revision right after the action; if anyone changed it since, the entry cannot apply.</summary>
        public long RevisionAfter;
        public bool Deleted;
        public int Tx, Ty, Rot, Scale;
        public long Color;
        public int Width;
        public bool Shared;
    }

    public sealed class CanvasHistory
    {
        public string Member;
        public List<CanvasUndoEntry> Undo = new List<CanvasUndoEntry>();
        public List<CanvasUndoEntry> Redo = new List<CanvasUndoEntry>();
    }

    /// <summary>The shared Canvas document (Addendum 02 §11 'CanvasDocument') with bounded history.</summary>
    public sealed class CanvasDocument
    {
        public ToyRunState Run = new ToyRunState();
        public long Revision;
        public long ObjectCounter;
        public int SheetCounter;
        public List<CanvasSheet> Sheets = new List<CanvasSheet>();
        public List<CanvasCheckpoint> Checkpoints = new List<CanvasCheckpoint>();
        public List<CanvasHistory> Histories = new List<CanvasHistory>();
        public ConsentProposal Proposal;
        public ConsentProposal LastProposal;

        public CanvasSheet Sheet(string id)
        {
            foreach (CanvasSheet s in Sheets) if (s.SheetId == id) return s;
            return null;
        }
    }

    /// <summary>Canvas text is data, never UI markup or code (Addendum 02 §11).</summary>
    public static class CanvasText
    {
        /// <summary>Accepts 1..80 UTF-16 units of printable text; rejects control characters, bidi overrides and broken surrogates.</summary>
        public static bool IsValid(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > CanvasLimits.MaxTextLength) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 0x20 || (c >= 0x7F && c <= 0x9F)) return false;
                if (c == '﻿' || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩')) return false;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                    i++;
                }
                else if (char.IsLowSurrogate(c)) return false;
            }
            return true;
        }

        /// <summary>For TextMeshPro-style rich text: wraps the literal text so no tag inside it can be interpreted.</summary>
        public static string ForRichText(string text) =>
            "<noparse>" + (text ?? "").Replace("</noparse>", "</​noparse>") + "</noparse>";

        /// <summary>For HTML-like surfaces (logs, web views).</summary>
        public static string ForHtml(string text)
        {
            var sb = new StringBuilder((text ?? "").Length + 8);
            foreach (char c in text ?? "")
                switch (c)
                {
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            return sb.ToString();
        }
    }

    /// <summary>Shared geometry so the flat whiteboard and the display hood render identical marks.</summary>
    public static class CanvasGeometry
    {
        /// <summary>Anchor for rotation/scale: the centre of the object's untransformed bounds.</summary>
        public static Vec2 Anchor(CanvasObject o)
        {
            if (o.Points == null || o.Points.Length < 2) return Vec2.Zero;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int i = 0; i + 1 < o.Points.Length; i += 2)
            {
                if (o.Points[i] < minX) minX = o.Points[i];
                if (o.Points[i] > maxX) maxX = o.Points[i];
                if (o.Points[i + 1] < minY) minY = o.Points[i + 1];
                if (o.Points[i + 1] > maxY) maxY = o.Points[i + 1];
            }
            return new Vec2((minX + maxX) * 0.5, (minY + maxY) * 0.5);
        }

        /// <summary>Applies the object's rotation/scale about its anchor, then its translation (sheet units).</summary>
        public static Vec2 Transform(CanvasObject o, double x, double y)
        {
            Vec2 a = Anchor(o);
            double s = o.Scale / 1000.0;
            double r = o.Rot / 100.0 * ToyMath.Deg2Rad;
            double dx = (x - a.X) * s, dy = (y - a.Y) * s;
            double c = Math.Cos(r), sn = Math.Sin(r);
            return new Vec2(a.X + dx * c - dy * sn + o.Tx, a.Y + dx * sn + dy * c + o.Ty);
        }
    }

    /// <summary>Compact persisted form: JSON → deflate (bounded both ways) → base64.</summary>
    public static class CanvasCodec
    {
        static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Include,
            MaxDepth = 16,
        };

        public static byte[] Compress(CanvasDocument doc) =>
            DowntimeCodec.Deflate(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(doc, Settings)));

        public static string Encode(CanvasDocument doc)
        {
            byte[] z = Compress(doc);
            if (z.Length > CanvasLimits.MaxCompressedBytes) throw new InvalidOperationException("canvas document exceeds its compressed budget");
            return Convert.ToBase64String(z);
        }

        public static CanvasDocument Decode(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            if (base64.Length > CanvasLimits.MaxCompressedBytes * 4 / 3 + 8) throw new InvalidOperationException("canvas payload too large");
            byte[] raw = DowntimeCodec.Inflate(Convert.FromBase64String(base64), CanvasLimits.MaxInflatedBytes);
            return JsonConvert.DeserializeObject<CanvasDocument>(Encoding.UTF8.GetString(raw), Settings);
        }

        /// <summary>Hash of the committed drawing only (sheets and marks, not histories): equal for every view and after rejoin.</summary>
        public static string DrawingHash(CanvasDocument doc)
        {
            var sheets = new List<object>();
            foreach (CanvasSheet s in doc.Sheets)
            {
                var objs = new List<object>();
                foreach (CanvasObject o in s.Objects)
                    if (!o.Deleted)
                        objs.Add(new { o.Id, o.Author, o.Kind, o.Color, o.Width, o.Points, o.Text, o.StampId, o.Size, o.Tx, o.Ty, o.Rot, o.Scale });
                sheets.Add(new { s.SheetId, s.Epoch, objs });
            }
            return DowntimeCodec.Hash(JsonConvert.SerializeObject(sheets, Settings));
        }
    }
}
