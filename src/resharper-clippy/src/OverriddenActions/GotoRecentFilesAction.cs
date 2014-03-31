﻿using System.Collections.Generic;
using System.Linq;
using CitizenMatt.ReSharper.Plugins.Clippy.AgentApi;
using JetBrains.ActionManagement;
using JetBrains.Application;
using JetBrains.Application.DataContext;
using JetBrains.DataFlow;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Navigation.Occurences;
using JetBrains.ReSharper.Feature.Services.Navigation.Search;
using JetBrains.ReSharper.Feature.Services.Occurences;
using JetBrains.ReSharper.Feature.Services.Occurences.Presentation;
using JetBrains.ReSharper.Features.Environment.RecentFiles;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Services;
using JetBrains.UI.PopupMenu;
using JetBrains.UI.PopupWindowManager;
using JetBrains.Util;

namespace CitizenMatt.ReSharper.Plugins.Clippy.OverriddenActions
{
    public class GotoRecentFilesAction : IActionHandler
    {
        private readonly Lifetime lifetime;
        private readonly Agent agent;
        private readonly ISolution solution;
        private readonly IShellLocks shellLocks;
        private readonly IPsiFiles psiFiles;
        private readonly RecentFilesTracker tracker;
        private readonly OccurencePresentationManager presentationManager;
        private readonly MainWindowPopupWindowContext popupWindowContext;

        public GotoRecentFilesAction(Lifetime lifetime, Agent agent, ISolution solution,
            IShellLocks shellLocks, IPsiFiles psiFiles, RecentFilesTracker tracker,
            OccurencePresentationManager presentationManager, MainWindowPopupWindowContext popupWindowContext)
        {
            this.lifetime = lifetime;
            this.agent = agent;
            this.solution = solution;
            this.shellLocks = shellLocks;
            this.psiFiles = psiFiles;
            this.tracker = tracker;
            this.presentationManager = presentationManager;
            this.popupWindowContext = popupWindowContext;
        }

        public bool Update(IDataContext context, ActionPresentation presentation, DelegateUpdate nextUpdate)
        {
            return nextUpdate();
        }

        public void Execute(IDataContext context, DelegateExecute nextExecute)
        {
            ShowLocations(tracker.FileLocations, tracker.CurrentFile, "Recent Files", false);
        }

        // ReSharper disable ConvertToLambdaExpression
        private void ShowLocations(IList<FileLocationInfo> locations, FileLocationInfo currentLocation, string caption, bool bindToPsi)
        {
            var lifetimeDefinition = Lifetimes.Define(lifetime);

            if (!Enumerable.Any(locations))
            {
                agent.ShowBalloon(lifetimeDefinition.Lifetime, caption, 
                    string.Format("There are no {0}.", caption.ToLowerInvariant()), null,
                    new[] {"OK"}, false, balloonLifetime =>
                    {
                        agent.ButtonClicked.Advise(balloonLifetime, _ => lifetimeDefinition.Terminate());
                    });
                return;
            }

            var options = new List<BalloonOption>();
            foreach (var locationInfo in locations.Distinct().Where(l => l.ProjectFile.IsValid() || l.FileSystemPath != null))
            {
                var descriptor = new SimpleMenuItem();

                var occurence = GetOccurence(locationInfo, bindToPsi);
                if (occurence != null)
                {
                    presentationManager.DescribeOccurence(descriptor, occurence, null);
                    var enabled = locationInfo != currentLocation;
                    options.Add(new BalloonOption(descriptor.Text + string.Format(" ({0})", descriptor.ShortcutText), false, enabled, locationInfo));
                }
            }

            agent.ShowBalloon(lifetimeDefinition.Lifetime, caption, string.Empty, options, new[] { "Done" }, true,
                balloonLifetime =>
                {
                    agent.BalloonOptionClicked.Advise(balloonLifetime, o =>
                    {
                        lifetimeDefinition.Terminate();

                        var locationInfo = o as FileLocationInfo;
                        if (locationInfo == null)
                            return;

                        shellLocks.ExecuteOrQueueReadLock("GotoRecentFiles", () =>
                        {
                            using (ReadLockCookie.Create())
                            {
                                psiFiles.CommitAllDocuments();
                                var occurence = GetOccurence(locationInfo, bindToPsi);
                                if (occurence != null)
                                    occurence.Navigate(solution, popupWindowContext.Source, true);
                            }
                        });
                    });

                    agent.ButtonClicked.Advise(balloonLifetime, _ => lifetimeDefinition.Terminate());
                });
        }
        // ReSharper restore ConvertToLambdaExpression

        private static IOccurence GetOccurence(FileLocationInfo location, bool bindToPsi)
        {
            var projectFile = location.ProjectFile;

            if (bindToPsi)
            {
                var sourceFile = projectFile.ToSourceFile();
                if (sourceFile != null)
                {
                    DeclaredElementEnvoy<INamespace> boundNamespace;
                    DeclaredElementEnvoy<ITypeElement> boundTypeElement;
                    DeclaredElementEnvoy<ITypeMember> boundTypeMember;
                    TextControlToPsi.BindToPsi(sourceFile, new TextRange(location.CaretOffset), out boundTypeMember, out boundTypeElement, out boundNamespace);
                    if (boundTypeMember != null || boundTypeElement != null)
                    {
                        var declaredElement = ((IDeclaredElementEnvoy)boundTypeMember ?? boundTypeElement).GetValidDeclaredElement();
                        if (declaredElement != null)
                            return new CustomRangeOccurence(sourceFile, new DocumentRange(sourceFile.Document, new TextRange(location.CaretOffset)), new OccurencePresentationOptions { ContainerStyle = ContainerDisplayStyle.File });
                    }
                }
            }

            //project file can loose its owner project (i.e. micsFilesProject) during provision tab navigation
            if (projectFile.GetProject() == null)
                return new DecompiledFileOccurence(location.FileSystemPath, new TextRange(location.CaretOffset), location.CachedPresentation);

            if (projectFile.IsValid())
                return new ProjectItemOccurence(projectFile, new OccurencePresentationOptions { ContainerStyle = ContainerDisplayStyle.NoContainer });

            return null;
        }
    }
}