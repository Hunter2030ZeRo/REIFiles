// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.IO;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Files.App.Services.SizeProvider
{
	public sealed partial class CachedSizeProvider : ISizeProvider
	{
		private readonly ConcurrentDictionary<string, ulong> sizes = new();

		public event EventHandler<SizeChangedEventArgs>? SizeChanged;

		public Task CleanAsync() => Task.CompletedTask;

		public Task ClearAsync()
		{
			sizes.Clear();
			return Task.CompletedTask;
		}

		public async Task UpdateAsync(string path, CancellationToken cancellationToken)
		{
			await Task.Yield();
			if (!sizes.ContainsKey(path))
				RaiseSizeChanged(path, 0, SizeChangedValueState.None);

			var stopwatch = Stopwatch.StartNew();
			ulong size = await Calculate(path);

			sizes[path] = size;
			RaiseSizeChanged(path, size, SizeChangedValueState.Final);

			async Task<ulong> Calculate(string path, int level = 0)
			{
				if (string.IsNullOrEmpty(path))
					return 0;

				static unsafe (FindCloseSafeHandle, WIN32_FIND_DATAW) FindFirstFileEx(string path)
				{
					WIN32_FIND_DATAW initialFindData = default;
					var hFile = PInvoke.FindFirstFileEx($"{path}{Path.DirectorySeparatorChar}*.*", FINDEX_INFO_LEVELS.FindExInfoBasic,
						&initialFindData, FINDEX_SEARCH_OPS.FindExSearchNameMatch, FIND_FIRST_EX_FLAGS.FIND_FIRST_EX_LARGE_FETCH);
					var findData = initialFindData;
					return (hFile, findData);
				}
				(FindCloseSafeHandle hFile, WIN32_FIND_DATAW findData) = FindFirstFileEx(path);
				using FindCloseSafeHandle findHandleScope = hFile;

				ulong size = 0;
				int processedEntries = 0;

				if (!hFile.IsInvalid)
				{
					do
					{
						if (((FileAttributes)findData.dwFileAttributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
							// Skip symbolic links and junctions
							continue;

						bool isDirectory = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Directory) is FileAttributes.Directory;
						if (!isDirectory)
						{
							size += (ulong)findData.GetSize();
						}
						else
						{
							string fileName = findData.cFileName.ToString();
							if (fileName is not "." and not "..")
							{
								var childPath = Path.Combine(path, fileName);
								var childSize = await Calculate(childPath, level + 1);
								size += childSize;
								if (level <= 3)
									sizes[childPath] = childSize;
							}
						}

						if (level <= 3 && ++processedEntries % 64 == 0)
							await Task.Yield();
						if (level is 0 && stopwatch.ElapsedMilliseconds > 500)
						{
							// Limit updates to every 0.5 seconds to prevent crashes due to frequent updates
							stopwatch.Restart();
							RaiseSizeChanged(path, size, SizeChangedValueState.Intermediate);
						}

						if (cancellationToken.IsCancellationRequested)
							break;
					} while (PInvoke.FindNextFile(hFile, out findData));
				}
				return size;
			}
		}

		public bool TryGetSize(string path, out ulong size) => sizes.TryGetValue(path, out size);

		public void Dispose() { }

		private void RaiseSizeChanged(string path, ulong newSize, SizeChangedValueState valueState)
			=> SizeChanged?.Invoke(this, new SizeChangedEventArgs(path, newSize, valueState));
	}
}
