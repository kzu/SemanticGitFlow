﻿namespace SemanticGitFlow
{
	using Microsoft.Build.Utilities;

	#region Using

	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.RegularExpressions;

	#endregion

	/*
    ============================================================
              ParseTags Task
	
        [IN]
        Input	   - The input text to parse the tags from, from 
		             a previous run of git.
		HeadTag    - The current head tag.
					 
        [OUT]
		Tags       - An item list containing items with:
		             ItemSpec = Tag
					     Text = Tag description
						 Range = The commit range for the tag 
						         WRT the previous tag.
	============================================================
	*/
	public class ParseSemanticTags : Task
	{
		#region Input

		public string Input { get; set; }
		public string HeadTag { get; set; }

		#endregion

		#region Output

		public Microsoft.Build.Framework.ITaskItem[] Tags { get; set; }

		#endregion

		public override bool Execute()
		{
			#region Code

			var semanticGitExpression = new Regex(@"^(?<Prefix>v)?(?<Major>\d+)\.(?<Minor>\d+)\.(?<Patch>\d+)(-(?<Revision>\d+)-g(?<Commit>[0-9a-z]+))?$",
				RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture);

			var match = semanticGitExpression.Match(HeadTag);
			if (!match.Success)
			{
				Log.LogError("Current head tag '{0}' does not comply with SemVer 2.0 specification (with optional 'v' prefix).", HeadTag);
				return false;
			}

			var patch = int.Parse(match.Groups["Patch"].Value);

			// If there are commits on top, we add them to the patch number.
			if (match.Groups["Revision"].Success)
				patch += int.Parse(match.Groups["Revision"].Value);

			var headVersion = match.Groups["Major"].Value + "." + match.Groups["Minor"].Value + "." + patch;
			var headRelease = match.Groups["Commit"].Success ?
				"-" + match.Groups["Commit"].Value : "";

			var allTags = Input
				.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(line => line.IndexOf(' ') == -1 ?
					new[] { line, "" } :
					new[] { 
						line.Substring(0, line.IndexOf(' ')), 
						line.Substring(line.IndexOf(' ') + 1).Trim()
					})
				.Select(tag => new
				{
					Item = new TaskItem(tag[0], new Dictionary<string, string> 
					{ 
						{ "Title", tag[0] }, 
						{ "Description", tag[1].Length == 0 ? "" : "- " + tag[1] },
						{ "IsHead", "false" },
					}),
					Match = semanticGitExpression.Match(tag[0])
				})
				.Select(tag =>
				{
					tag.Item.SetMetadata("IsSemantic", tag.Match.Success.ToString().ToLowerInvariant());
					if (tag.Match.Success)
					{
						// Set the version metadata on all tags to allow proper sorting.
						tag.Item.SetMetadata("Version",
							tag.Match.Groups["Major"].Value + "." +
							tag.Match.Groups["Minor"].Value + "." +
							tag.Match.Groups["Patch"].Value);
					}

					return tag.Item;
				})
				.ToList();

			// Warn and skip non-semantic tags.
			var nonSemanticTags = allTags.Where(t => t.GetMetadata("IsSemantic") == "false").Select(t => t.ItemSpec).ToArray();
			if (nonSemanticTags.Length != 0)
			{
				Log.LogWarning("The following tags are not semantic and will be skipped from parsing: {0}", string.Join(", ", nonSemanticTags));
			}

			allTags.RemoveAll(t => t.GetMetadata("IsSemantic") == "false");

			// Annotate the revision range for each tag.
			for (int i = 1; i < allTags.Count; i++)
			{
				allTags[i].SetMetadata("Range", allTags[i - 1].ItemSpec + ".." + allTags[i].ItemSpec);
			}

			// The oldest tag has no range, it effectively contains all commits 
			// up to its definition.
			if (allTags.Count > 0)
				allTags[0].SetMetadata("Range", allTags[0].ItemSpec);

			// If the head tag doesn't exist in the list of tags, 
			// it means there are new commits on top of the newest tag, 
			// so we add it as a new pseudo tag, HEAD
			if (!allTags.Any(t => t.ItemSpec == HeadTag))
			{
				var tagSpec = HeadTag;
				var parentTag = HeadTag.Substring(0, HeadTag.IndexOf('-'));
				var parent = allTags.First(t => t.ItemSpec == parentTag);
				var tag = new TaskItem(HeadTag, new Dictionary<string, string> 
					{ 
						{ "Title", match.Groups["Prefix"].Value + headVersion },
						{ "Description", "- HEAD" },
						{ "IsHead", "true" },
						{ "IsSemantic", "true"}, 
						{ "Version", headVersion },
						{ "Range", parent.ItemSpec + ".." + HeadTag },
					});

				allTags.Add(tag);
			}

			var version = new Version(headVersion);

			Tags = allTags
				// Only include tags that are smaller or equal than the current branch head
				.Where(t => new Version(t.GetMetadata("Version")) <= version)
				// Finally, sort by version.
				.OrderByDescending(t => t.GetMetadata("Version"))
				.ToArray();

			#endregion

			return true;
		}
	}
}