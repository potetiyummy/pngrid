using System.Collections.Generic;

namespace PngGridViewer;

/// <summary>
/// 16x16セルの位置（画像上のセル番号）。
/// </summary>
public readonly record struct CellPos(int Column, int Row);

/// <summary>
/// .png16 ファイルの内容。画像のパスと、緑に塗られたセルの一覧を保存する（それ以外は白）。
/// </summary>
public sealed class Png16Project
{
    public int Version { get; set; } = 1;

    public string ImagePath { get; set; } = "";

    public List<CellPos> GreenCells { get; set; } = new();
}
