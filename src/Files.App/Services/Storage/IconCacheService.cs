// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;
using System.IO;

namespace Files.App.Services
{
	internal sealed class IconCacheService : IIconCacheService
	{
		// Dummy path to generate generic icons for folders, executables, and shortcuts.
		private static readonly string _dummyPath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "x46696c6573");

		private readonly ConcurrentDictionary<string, byte[]?> _cache = new();
		private readonly ConcurrentDictionary<string, BitmapImage> _imageCache = new();
		private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _pendingIcons = new();
		private readonly object _cacheSync = new();
		private int _cacheGeneration;

		public async Task<byte[]?> GetIconAsync(string? itemPath, string? extension, bool isFolder, uint size)
		{
			var key = $"{(isFolder ? ":folder:" : (extension?.ToLowerInvariant() ?? ":noext:"))}:{size}";

			if (_cache.TryGetValue(key, out var cached))
				return cached;

			// Only the first request for each icon performs shell extraction.
			var generation = Volatile.Read(ref _cacheGeneration);
			var pendingKey = $"{generation}:{key}";
			var iconPath = isFolder ? Environment.SystemDirectory : (string.IsNullOrEmpty(extension) ? _dummyPath : _dummyPath + extension);
			var pending = _pendingIcons.GetOrAdd(pendingKey, _ => new Lazy<Task<byte[]?>>(
				() => FileThumbnailHelper.GetIconAsync(iconPath, size, isFolder, IconOptions.ReturnIconOnly)));

			try
			{
				var icon = await pending.Value;
				if (icon is not null)
				{
					lock (_cacheSync)
					{
						if (_cacheGeneration == generation)
							_cache.TryAdd(key, icon);
					}
				}

				return icon;
			}
			finally
			{
				_pendingIcons.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]?>>>(pendingKey, pending));
			}
		}

		public async Task<BitmapImage?> GetIconImageAsync(string? itemPath, string? extension, bool isFolder, uint size)
		{
			// BitmapImage has thread affinity, so only the main window's dispatcher may use this cache
			if (DispatcherQueue.GetForCurrentThread() is null)
				return null;

			var key = $"{(isFolder ? ":folder:" : (extension?.ToLowerInvariant() ?? ":noext:"))}:{size}";
			if (_imageCache.TryGetValue(key, out var cached))
				return cached;

			var data = await GetIconAsync(itemPath, extension, isFolder, size);
			if (data is null)
				return null;

			var image = await data.ToBitmapAsync();
			if (image is not null)
				_imageCache.TryAdd(key, image);

			return image;
		}

		public void Clear()
		{
			lock (_cacheSync)
			{
				Interlocked.Increment(ref _cacheGeneration);
				_cache.Clear();
				_imageCache.Clear();
				_pendingIcons.Clear();
			}
		}
	}
}
