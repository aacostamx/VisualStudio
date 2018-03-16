﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GitHub.Extensions;
using GitHub.Factories;
using GitHub.Logging;
using GitHub.Models;
using GitHub.Services;
using ReactiveUI;
using Serilog;

namespace GitHub.ViewModels.GitHubPane
{
    [Export(typeof(IPullRequestReviewAuthoringViewModel))]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class PullRequestReviewAuthoringViewModel : PanePageViewModelBase, IPullRequestReviewAuthoringViewModel
    {
        static readonly ILogger log = LogManager.ForContext<PullRequestReviewAuthoringViewModel>();

        readonly IPullRequestEditorService editorService;
        readonly IPullRequestSessionManager sessionManager;
        readonly IModelServiceFactory modelServiceFactory;
        IModelService modelService;
        IPullRequestSession session;
        IDisposable sessionSubscription;
        IPullRequestReviewModel model;
        IPullRequestModel pullRequestModel;
        string body;
        IReadOnlyList<IPullRequestReviewFileCommentViewModel> fileComments;

        [ImportingConstructor]
        public PullRequestReviewAuthoringViewModel(
            IPullRequestEditorService editorService,
            IPullRequestSessionManager sessionManager,
            IModelServiceFactory modelServiceFactory,
            IPullRequestFilesViewModel files)
        {
            Guard.ArgumentNotNull(editorService, nameof(editorService));
            Guard.ArgumentNotNull(sessionManager, nameof(sessionManager));
            Guard.ArgumentNotNull(modelServiceFactory, nameof(modelServiceFactory));
            Guard.ArgumentNotNull(files, nameof(files));

            this.editorService = editorService;
            this.sessionManager = sessionManager;
            this.modelServiceFactory = modelServiceFactory;

            Files = files;
        }

        /// <inheritdoc/>
        public ILocalRepositoryModel LocalRepository { get; private set; }

        /// <inheritdoc/>
        public string RemoteRepositoryOwner { get; private set; }

        /// <inheritdoc/>
        public IPullRequestReviewModel Model
        {
            get { return model; }
            private set { this.RaiseAndSetIfChanged(ref model, value); }
        }

        /// <inheritdoc/>
        public IPullRequestModel PullRequestModel
        {
            get { return pullRequestModel; }
            private set { this.RaiseAndSetIfChanged(ref pullRequestModel, value); }
        }

        /// <inheritdoc/>
        public IPullRequestFilesViewModel Files { get; }

        /// <inheritdoc/>
        public string Body
        {
            get { return body; }
            set { this.RaiseAndSetIfChanged(ref body, value); }
        }

        /// <inheritdoc/>
        public IReadOnlyList<IPullRequestReviewFileCommentViewModel> FileComments
        {
            get { return fileComments; }
            private set { this.RaiseAndSetIfChanged(ref fileComments, value); }
        }

        public ReactiveCommand<object> NavigateToPullRequest { get; }
        public ReactiveCommand<Unit> Submit { get; }

        public async Task InitializeAsync(
            ILocalRepositoryModel localRepository,
            IConnection connection,
            string owner,
            string repo,
            int pullRequestNumber,
            long pullRequestReviewId)
        {
            if (repo != localRepository.Name)
            {
                throw new NotSupportedException();
            }

            IsLoading = true;

            try
            {
                LocalRepository = localRepository;
                RemoteRepositoryOwner = owner;
                modelService = await modelServiceFactory.CreateAsync(connection);
                var pullRequest = await modelService.GetPullRequest(
                    RemoteRepositoryOwner,
                    LocalRepository.Name,
                    pullRequestNumber);
                await Load(pullRequest, pullRequestReviewId);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <inheritdoc/>
        public override async Task Refresh()
        {
            try
            {
                Error = null;
                IsBusy = true;
                var pullRequest = await modelService.GetPullRequest(
                    RemoteRepositoryOwner,
                    LocalRepository.Name,
                    PullRequestModel.Number);
                await Load(pullRequest, Model.Id);
            }
            catch (Exception ex)
            {
                log.Error(
                    ex,
                    "Error loading pull request review {Owner}/{Repo}/{Number}/{PullRequestReviewId} from {Address}",
                    RemoteRepositoryOwner,
                    LocalRepository.Name,
                    PullRequestModel.Number,
                    Model.Id,
                    modelService.ApiClient.HostAddress.Title);
                Error = ex;
                IsBusy = false;
            }
        }

        /// <inheritdoc/>
        public async Task Load(IPullRequestModel pullRequest, long pullRequestReviewId)
        {
            try
            {
                session = await sessionManager.GetSession(pullRequest);
                PullRequestModel = pullRequest;

                if (pullRequestReviewId > 0)
                {
                    Model = pullRequest.Reviews.Single(x => x.Id == Model.Id);
                    Body = Model.Body;
                }
                else
                {
                    Model = new PullRequestReviewModel
                    {
                        User = session.User,
                        State = PullRequestReviewState.Pending,
                    };
                    Body = string.Empty;
                }

                await Files.InitializeAsync(session, FilterComments);

                sessionSubscription?.Dispose();
                await UpdateFileComments();
                sessionSubscription = session.PullRequestChanged
                    .Skip(1)
                    .Subscribe(_ => UpdateFileComments().Forget());
            }
            finally
            {
                IsBusy = false;
            }
        }

        bool FilterComments(IInlineCommentThreadModel thread)
        {
            return thread.Comments.Any(x => x.PullRequestReviewId == Model.Id);
        }

        async Task UpdateFileComments()
        {
            var result = new List<PullRequestReviewFileCommentViewModel>();

            foreach (var file in await session.GetAllFiles())
            {
                foreach (var thread in file.InlineCommentThreads)
                {
                    foreach (var comment in thread.Comments)
                    {
                        if (comment.PullRequestReviewId == Model.Id)
                        {
                            result.Add(new PullRequestReviewFileCommentViewModel(
                                editorService,
                                session,
                                comment));
                        }
                    }
                }
            }

            FileComments = result;
        }
    }
}
