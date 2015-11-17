﻿using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using DevExpress.Mvvm;
using DevExpress.Mvvm.Native;
using DXVcs2Git.Core;
using DXVcs2Git.Core.Git;
using DXVcs2Git.Git;
using NGitLab.Models;

namespace DXVcs2Git.UI.ViewModels {
    public class MergeRequestsViewModel : BindableBase {
        readonly GitLabWrapper gitLabWrapper;
        readonly GitReaderWrapper gitReader;
        BranchViewModel selectedBranch;

        public Project Project { get; private set; }
        public IEnumerable<BranchViewModel> Branches {
            get { return GetProperty(() => Branches); }
            set { SetProperty(() => Branches, value); }
        }
        public IEnumerable<Branch> ProtectedBranches { get; set; }
        public bool HasEditableMergeRequest {
            get { return GetProperty(() => HasEditableMergeRequest); }
            private set { SetProperty(() => HasEditableMergeRequest, value); }
        }
        public BranchViewModel SelectedBranch {
            get { return this.selectedBranch; }
            set { SetProperty(ref this.selectedBranch, value, () => SelectedBranch); }
        }

        public ICommand UpdateCommand { get; private set; }

        public MergeRequestsViewModel(GitLabWrapper gitLabWrapper, GitReaderWrapper gitReader) {
            this.gitReader = gitReader;
            this.gitLabWrapper = gitLabWrapper;
            UpdateCommand = DelegateCommandFactory.Create(Update, CanUpdate);

            Update();
        }
        public void Update() {
            Project = gitLabWrapper.FindProject(this.gitReader.GetRemoteRepoPath());
            if (Project == null) {
                Log.Error("Can`t find project");
                return;
            }

            var mergeRequests = this.gitLabWrapper.GetMergeRequests(Project);
            var branches = this.gitLabWrapper.GetBranches(Project).ToList();
            ProtectedBranches = branches.Where(x => x.Protected).ToList();
            Branches = branches.Where(x => !x.Protected)
                .Select(x => new BranchViewModel(gitLabWrapper, this.gitReader, this, mergeRequests.FirstOrDefault(mr => mr.SourceBranch == x.Name), x)).ToList();
            SelectedBranch = Branches.FirstOrDefault();
            HasEditableMergeRequest = SelectedBranch.If(x => x.IsInEditingMergeRequest).ReturnSuccess();
        }
        bool CanUpdate() {
            return true;
        }
    }
}
