﻿using System;
using CommandLine;

namespace DXVcs2Git.UI.ViewModels {
    public class Options {
        [Option('s', "source", HelpText = "Source git branch name")]
        public string SourceBranch { get; set; }
        [Option('t', "target", HelpText = "Target git branch name")]
        public string TargetBranch { get; set; }
        [Option('r', "repo", Required = true, HelpText = "Http git repo path")]
        public string Repo { get; set; }
        [Option('d', "dir", HelpText = "Path to local git repo")]
        public string LocalFolder { get; set; }
        [Option('p', "password", Required = true, HelpText = "Token for gitlab cli")]
        public string Token { get; set; }

        public const string GitServer = @"http://litvinov-lnx";
    }
}