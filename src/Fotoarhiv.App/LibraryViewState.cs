using System;
using System.Collections.Generic;
using System.Linq;
using Fotoarhiv.Core;

namespace Fotoarhiv.App;

public enum SortDirection { NewestFirst, OldestFirst }

/// <summary>Session-only view and selection. No source/catalog writes or filesystem probes.</summary>
public sealed class LibraryViewState
{
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    private List<PhotoRecord> _catalog;
    private List<string> _sources;
    public IReadOnlyList<PhotoRecord> Catalog => _catalog;
    public IReadOnlyList<string> Sources => _sources;
    public PhotoSelection Selection { get; } = new();
    public SortDirection Sort { get; private set; }
    public string Search { get; private set; } = "";
    public int? Year { get; private set; }
    public int VisibleSourceCount => _sources.Count(IsSourceVisible);
    public LibraryViewState(IEnumerable<PhotoRecord> photos, IEnumerable<string> sources, SortDirection sort = SortDirection.NewestFirst)
    {
        _catalog = Unique(photos); _sources = sources.DistinctBy(PathIdentity.Normalize).ToList(); Sort = sort;
    }
    private static List<PhotoRecord> Unique(IEnumerable<PhotoRecord> photos) => photos.DistinctBy(p => PathIdentity.Normalize(p.Path)).ToList();
    public List<PhotoRecord> SourcePhotos => _catalog.Where(p => _sources.Any(root => IsSourceVisible(root) && PathIdentity.BelongsTo(p.Path, root))).ToList();
    public List<PhotoRecord> Photos
    {
        get
        {
            IEnumerable<PhotoRecord> query = SourcePhotos;
            if (Search.Length > 0) query = query.Where(p => p.Path.Contains(Search, StringComparison.OrdinalIgnoreCase));
            if (Year.HasValue) query = query.Where(p => p.TakenAt.Year == Year.Value);
            var ordered = Sort == SortDirection.OldestFirst ? query.OrderBy(p => p.TakenAt) : query.OrderByDescending(p => p.TakenAt);
            return ordered.ThenBy(p => PathIdentity.Normalize(p.Path), StringComparer.Ordinal).ToList();
        }
    }
    public bool IsSourceVisible(string root) => !_hidden.Contains(PathIdentity.Normalize(root));
    public void SetSourceVisible(string root, bool visible)
    {
        if (!_sources.Any(p => PathIdentity.Normalize(p) == PathIdentity.Normalize(root))) return;
        bool changed = visible ? _hidden.Remove(PathIdentity.Normalize(root)) : _hidden.Add(PathIdentity.Normalize(root));
        if (changed) Selection.Clear();
    }
    public void ShowAllSources() { if (_hidden.Count > 0) { _hidden.Clear(); Selection.Clear(); } }
    public void HideAllSources() { foreach (var root in _sources) _hidden.Add(PathIdentity.Normalize(root)); Selection.Clear(); }
    public void SetSearch(string search) { search = search.Trim(); if (search != Search) { Search = search; Selection.Clear(); } }
    public void SetYear(int? year) { if (year != Year) { Year = year; Selection.Clear(); } }
    public void SetSort(SortDirection sort) { if (sort != Sort) { Sort = sort; Selection.Clear(); } }
    public void Refresh(IEnumerable<PhotoRecord> photos, IEnumerable<string> sources)
    {
        _catalog = Unique(photos); _sources = sources.DistinctBy(PathIdentity.Normalize).ToList(); Selection.Clear();
    }
    public List<List<PhotoRecord>> VisibleDuplicates(IEnumerable<IReadOnlyList<PhotoRecord>> groups)
    {
        var visible = Photos.Select(p => PathIdentity.Normalize(p.Path)).ToHashSet(StringComparer.Ordinal);
        return groups.Select(g => g.Where(p => visible.Contains(PathIdentity.Normalize(p.Path))).DistinctBy(p => PathIdentity.Normalize(p.Path)).ToList()).Where(g => g.Count >= 2).ToList();
    }
}

public sealed class PhotoSelection
{
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private string? _anchor;
    public int Count => _paths.Count;
    public bool Contains(string path) => _paths.Contains(PathIdentity.Normalize(path));
    public void Toggle(string path)
    {
        var key = PathIdentity.Normalize(path); if (!_paths.Add(key)) _paths.Remove(key); _anchor = key;
    }
    public void SelectRange(string target, IReadOnlyList<PhotoRecord> order, bool additive = false)
    {
        var keys = order.Select(p => PathIdentity.Normalize(p.Path)).ToList();
        int last = keys.IndexOf(PathIdentity.Normalize(target)); if (last < 0) return;
        int first = _anchor is null ? last : keys.IndexOf(_anchor); if (first < 0) first = last;
        if (!additive) _paths.Clear();
        for (int i = Math.Min(first, last); i <= Math.Max(first, last); i++) _paths.Add(keys[i]);
        _anchor ??= keys[last];
    }
    public void Clear() { _paths.Clear(); _anchor = null; }
}

public sealed class ViewerSequence
{
    public IReadOnlyList<PhotoRecord> Photos { get; }
    public int Index { get; private set; }
    public PhotoRecord Current => Photos[Index];
    public bool HasPrevious => Index > 0;
    public bool HasNext => Index + 1 < Photos.Count;
    public ViewerSequence(IReadOnlyList<PhotoRecord> photos, string selected)
    {
        Photos = photos.ToArray();
        Index = Array.FindIndex(Photos.ToArray(), p => PathIdentity.Normalize(p.Path) == PathIdentity.Normalize(selected));
        if (Index < 0) throw new ArgumentException("Selected photo must belong to the current view", nameof(selected));
    }
    public bool Move(int offset) { int next = Index + offset; if (next < 0 || next >= Photos.Count) return false; Index = next; return true; }
}
