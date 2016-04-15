﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PX.Data;
using System.IO;
using System.Web.Compilation;
using PX.Common;
using Box.V2.Exceptions;
using System.Net;
using Newtonsoft.Json;

namespace PX.SM.BoxStorageProvider
{
    public class FileHandler : PXGraph<FileHandler>
    {
        public const string MiscellaneousFolderScreenId = "00000000";

        public PXSelect<BoxFolderCache, Where<BoxFolderCache.screenID, Equal<Required<BoxFolderCache.screenID>>>> FoldersByScreen;
        public PXSelect<BoxFolderCache, Where<BoxFolderCache.folderID, Equal<Required<BoxFolderCache.folderID>>>> FoldersByFolderID;
        public PXSelect<BoxFolderCache, Where<BoxFolderCache.refNoteID, Equal<Required<BoxFolderCache.refNoteID>>>> FoldersByNote;
        public PXSelect<BoxFileCache, Where<BoxFileCache.blobHandler, Equal<Required<BoxFileCache.blobHandler>>>> FilesByBlobHandler;

        // Views needed to synchronize and manage file list
        public PXSelectJoin<BoxFileCache, InnerJoin<UploadFileRevisionNoData, On<UploadFileRevisionNoData.blobHandler, Equal<BoxFileCache.blobHandler>>, InnerJoin<UploadFile, On<UploadFile.fileID, Equal<UploadFileRevisionNoData.fileID>, And<UploadFile.lastRevisionID, Equal<UploadFileRevisionNoData.fileRevisionID>>>, InnerJoin<NoteDoc, On<NoteDoc.fileID, Equal<UploadFile.fileID>>>>>, Where<NoteDoc.noteID, Equal<Required<NoteDoc.noteID>>>> FilesByNoteID;
        public PXSelect<UploadFile> UploadFiles;
        public PXSelect<UploadFileRevision> UploadFileRevisions;
        public PXSelect<NoteDoc> NoteDocs;

        public BoxUtils.FileFolderInfo GetOrCreateBoxFolderForNoteID(Guid refNoteID)
        {
            var tokenHandler = PXGraph.CreateInstance<UserTokenHandler>();
            var bfc = (BoxFolderCache)this.FoldersByNote.Select(refNoteID);
            if (bfc == null)
            {
                // Folder doesn't exist in cache; retrieve it from Box or create it if it doesn't exist.
                EntityHelper entityHelper = new EntityHelper(this);
                object entityRow = entityHelper.GetEntityRow(new Guid?(refNoteID));
                Type primaryGraphType = entityHelper.GetPrimaryGraphType(entityRow, false);

                if (primaryGraphType == null) throw new PXException(Messages.PrimaryGraphForNoteIDNotFound, refNoteID);
                if (entityRow == null) throw new PXException(Messages.EntityRowForNoteIDNotFound, refNoteID);

                // Get screen main folder, foe example "Customers (AR303000)"
                PXSiteMapNode siteMapNode = PXSiteMap.Provider.FindSiteMapNode(primaryGraphType);
                if (siteMapNode == null) throw new PXException(Messages.SiteMapNodeForGraphNotFound, primaryGraphType.FullName);
                var bfcParent = (BoxFolderCache)this.FoldersByScreen.Select(siteMapNode.ScreenID);
                if (bfcParent == null) throw new PXException(Messages.ScreenMainFolderDoesNotExist, siteMapNode.ScreenID);


                // Try to find folder; if it doesn't exist, create it.
                string folderName = GetFolderNameForEntityRow(entityRow);

                try
                {
                    BoxUtils.FileFolderInfo folderInfo = BoxUtils.FindFolder(tokenHandler, bfcParent.FolderID, folderName).Result;

                    if (folderInfo == null)
                    {
                        // Folder doesn't exist on Box, create it.
                        folderInfo = BoxUtils.CreateFolder(tokenHandler, folderName, bfcParent.FolderID).Result;
                    }

                    // Store the folder info in our local cache for future reference
                    bfc = (BoxFolderCache)this.FoldersByScreen.Cache.CreateInstance();
                    bfc.FolderID = folderInfo.ID;
                    bfc.ParentFolderID = folderInfo.ParentFolderID;
                    bfc.RefNoteID = refNoteID;
                    bfc.LastModifiedDateTime = null; // To force initial sync of Box file list with record file ilst
                    bfc = this.FoldersByNote.Insert(bfc);
                    this.Actions.PressSave();

                    return folderInfo;
                }
                catch (AggregateException ae)
                {
                    ae.Handle((e) =>
                    {
                        PXTrace.WriteError(e);
                        var boxException = e as BoxException;
                        if (boxException != null && boxException.StatusCode == HttpStatusCode.NotFound)
                        {
                            throw (new PXException(string.Format(Messages.BoxFolderNotFoundRunSynchAgain, bfcParent.ScreenID), boxException));
                        }

                        throw e;
                    });

                    return null;
                }
            }
            else
            {
                try
                {
                    // Folder was found in BoxFolderCache, retrieve it by ID
                    BoxUtils.FileFolderInfo folderInfo = BoxUtils.GetFolderInfo(tokenHandler, bfc.FolderID).Result;
                    return folderInfo;
                }
                catch (AggregateException ae)
                {
                    ae.Handle((e) =>
                    {
                        PXTrace.WriteError(e);
                        var boxException = e as BoxException;
                        if (boxException != null && boxException.StatusCode == HttpStatusCode.NotFound)
                        {
                            using (new PXConnectionScope())
                            {
                                // Delete entry from BoxFolderCache so that it gets created again.
                                this.FoldersByNote.Delete(bfc);
                                this.Actions.PressSave();

                                throw new PXException(Messages.BoxFolderNotFoundTryAgain, bfc.FolderID, e);
                            }
                        }

                        return false;
                    });

                    return null;
                }
            }
        }

        private string GetFolderNameForEntityRow(object entityRow)
        {
            PXCache cache = this.Caches[entityRow.GetType()];
            string[] keyValues = new string[cache.BqlKeys.Count];
            for (int i = 0; i < cache.BqlKeys.Count; i++)
            {
                keyValues[i] = cache.GetValue(entityRow, cache.BqlKeys[i].Name).ToString();
            }
            return BoxUtils.CleanFileOrFolderName(String.Join(" ", keyValues));
        }

        public byte[] DownloadFileFromBox(Guid blobHandler)
        {
            var tokenHandler = PXGraph.CreateInstance<UserTokenHandler>();
            BoxFileCache bfc = GetFileInfoFromCache(blobHandler);
            try
            {
                return BoxUtils.DownloadFile(tokenHandler, bfc.FileID).Result;
            }
            catch(AggregateException ae)
            {
                ae.Handle((e) => 
                {
                    PXTrace.WriteError(e);
                    var be = e as BoxException;
                    if(be != null && be.StatusCode == HttpStatusCode.NotFound)
                    {
                        
                        throw new PXException(Messages.BoxFileNotFound, e);
                    }

                    return false;
                });

                return new byte[0];
            }
        }

        public void DeleteFileFromBox(Guid blobHandler)
        {
            //TODO: Test what happens if file has been deleted from Box but is still in Acumatica. Needs to show a proper exception.
            var tokenHandler = PXGraph.CreateInstance<UserTokenHandler>();
            BoxFileCache bfc = GetFileInfoFromCache(blobHandler);
            BoxUtils.DeleteFile(tokenHandler, bfc.FileID);
        }

        public Guid SaveFileToBoxAndUpdateFileCache(byte[] data, PXBlobStorageContext saveContext)
        {
            BoxUtils.FileFolderInfo boxFile = null;
            Guid blobHandlerGuid = Guid.NewGuid();

            var tokenHandler = PXGraph.CreateInstance<UserTokenHandler>();
            
            if (saveContext == null || saveContext.FileInfo == null || !saveContext.NoteID.HasValue)
            {
                var fileName = string.Empty;
                if (saveContext?.FileInfo?.Name == null)
                {
                    fileName = blobHandlerGuid.ToString();
                }
                else
                {
                    fileName = BoxUtils.CleanFileOrFolderName(saveContext.FileInfo.Name);
                    fileName = $"{Path.GetFileNameWithoutExtension(fileName)} ({blobHandlerGuid.ToString()}){Path.GetExtension(fileName)}";
                }

                //We don't know on which screen this file belongs. We'll have to save it in miscellaneous files folder.
                BoxUtils.FileFolderInfo boxFolder = GetMiscellaneousFolder();
                boxFile = BoxUtils.UploadFile(tokenHandler, boxFolder.ID, fileName, data).Result;
            }
            else
            {
                var fileName = BoxUtils.CleanFileOrFolderName(Path.GetFileName(saveContext.FileInfo.Name));
                BoxUtils.FileFolderInfo boxFolder = GetOrCreateBoxFolderForNoteID(saveContext.NoteID.Value);
                boxFile = BoxUtils.UploadFile(tokenHandler, boxFolder.ID, fileName, data).Result;

                if (!String.IsNullOrEmpty(saveContext.FileInfo.Comment))
                {
                    BoxUtils.SetFileDescription(tokenHandler, boxFile.ID, saveContext.FileInfo.Comment).Wait();
                }
            }

            var bfc = (BoxFileCache)this.FilesByBlobHandler.Cache.CreateInstance();
            bfc.BlobHandler = blobHandlerGuid;
            bfc.FileID = boxFile.ID;
            bfc.ParentFolderID = boxFile.ParentFolderID;
            bfc = this.FilesByBlobHandler.Insert(bfc);
            this.Actions.PressSave();

            return blobHandlerGuid;
        }

        public BoxUtils.FileFolderInfo GetBoxFileInfoForFileID(Guid fileID)
        {
            Guid blobHandler = GetBlobHandlerForFileID(fileID);
            BoxFileCache bfc = GetFileInfoFromCache(blobHandler);
            var tokenHandler = PXGraph.CreateInstance<UserTokenHandler>();

            BoxUtils.FileFolderInfo fileInfo = BoxUtils.GetFileInfo(tokenHandler, bfc.FileID).Result;
            if (fileInfo == null) throw new PXException(Messages.FileNotFoundInBox, bfc.FileID);

            return fileInfo;
        }

        public BoxFileCache GetFileInfoFromCache(Guid blobHandler)
        {
            var file = (BoxFileCache)FilesByBlobHandler.Select(blobHandler);
            if (file == null) throw new PXException(Messages.FileNotFoundInBoxFileCache, blobHandler);
            return file;
        }

        public BoxUtils.FileFolderInfo GetMiscellaneousFolder()
        {
            var bfc = (BoxFolderCache)FoldersByScreen.Select(MiscellaneousFolderScreenId);
            if (bfc == null)
            {
                throw new PXException(Messages.MiscFolderNotFoundRunSynchAgain);
            }
            else
            {
                try
                {
                    // Folder was found in BoxFolderCache, retrieve it by ID
                    var tokenHandler = PXGraph.CreateInstance<UserTokenHandler>();
                    BoxUtils.FileFolderInfo folderInfo = BoxUtils.GetFolderInfo(tokenHandler, bfc.FolderID).Result;
                    return folderInfo;
                }
                catch (AggregateException ae)
                {
                    ae.Handle((e) =>
                    {
                        PXTrace.WriteError(e);
                        var boxException = e as BoxException;
                        if (boxException != null && boxException.StatusCode == HttpStatusCode.NotFound)
                        {
                            using (new PXConnectionScope())
                            {
                                // Delete entry from BoxFolderCache so that it gets created again.
                                this.FoldersByScreen.Delete(bfc);
                                this.Actions.PressSave();

                                throw new PXException(Messages.MiscFolderNotFoundRunSynchAgain, bfc.FolderID, e);
                            }
                        }

                        return false;
                    });

                    return null;
                }

                
            }
        }

        public void SynchronizeScreen(Screen screen, BoxUtils.FileFolderInfo rootFolder)
        {
            string folderName = string.Format("{0} ({1})", (object)BoxUtils.CleanFileOrFolderName(screen.Name), (object)screen.ScreenID);

            BoxFolderCache screenFolderInfo = this.FoldersByScreen.Select(screen.ScreenID);
            BoxUtils.FileFolderInfo folderInfo = null;
            var tokenHandler = PXGraph.CreateInstance<UserTokenHandler>();

            if (screenFolderInfo != null)
            {
                try
                {
                    folderInfo = BoxUtils.GetFolderInfo(tokenHandler, screenFolderInfo.FolderID).Result;
                }
                catch (AggregateException ae)
                {
                    ae.Handle((e) =>
                    {
                        PXTrace.WriteError(e);
                        var boxException = e as BoxException;
                        if (boxException != null && boxException.StatusCode == HttpStatusCode.NotFound)
                        {
                            // Folder no longer exist on Box - it may have been deleted on purpose by the user. Remove it from cache so it is recreated on the next run.
                            screenFolderInfo = this.FoldersByScreen.Delete(screenFolderInfo);
                            this.Actions.PressSave();
                            throw new PXException(Messages.BoxFolderNotFoundRunSynchAgain, screenFolderInfo.ScreenID);
                        }
                        else
                        {
                            return false;
                        }
                    });
                }
            }

            if (folderInfo == null)
            {
                // Folder wasn't found, try finding it by name in the root folder.
                folderInfo = BoxUtils.FindFolder(tokenHandler, rootFolder.ID, folderName).Result;
            }

            if (folderInfo == null)
            {
                // Folder doesn't exist at all - create it
                folderInfo = BoxUtils.CreateFolder(tokenHandler, folderName, rootFolder.ID).Result;
            }

            if (screenFolderInfo == null)
            {
                screenFolderInfo = (BoxFolderCache)this.FoldersByScreen.Cache.CreateInstance();
                screenFolderInfo.FolderID = folderInfo.ID;
                screenFolderInfo.ParentFolderID = folderInfo.ParentFolderID;
                screenFolderInfo.ScreenID = screen.ScreenID;
                screenFolderInfo.LastModifiedDateTime = null; //To force initial sync
                screenFolderInfo = this.FoldersByScreen.Insert(screenFolderInfo);
            }

            // We don't synchronize the miscellaneous files folder, since we can't easily identify the corresponding NoteID from folder
            if (screen.ScreenID != FileHandler.MiscellaneousFolderScreenId && screenFolderInfo.LastModifiedDateTime != folderInfo.ModifiedAt)
            {
                SynchronizeFolderContentsWithScreen(screenFolderInfo);
                screenFolderInfo.LastModifiedDateTime = folderInfo.ModifiedAt;
                this.FoldersByScreen.Update(screenFolderInfo);
                this.Actions.PressSave();
            }
        }

        private void SynchronizeFolderContentsWithScreen(BoxFolderCache screenFolderInfo)
        {
            // Retrieve top-level folder list
            // TODO: we'll have to go one level deeper when support for customizable folder structure will be added
            var tokenHandler = PXGraph.CreateInstance<UserTokenHandler>();
            List<BoxUtils.FileFolderInfo> list = BoxUtils.GetFolderList(tokenHandler, screenFolderInfo.FolderID, 1).Result;
            foreach (BoxUtils.FileFolderInfo folderInfo in list)
            {
                BoxFolderCache bfc = this.FoldersByFolderID.Select(folderInfo.ID);
                if (bfc == null)
                {
                    // We've never seen this folder; sync it
                    Guid? refNoteID = FindMatchingNoteIDForFolder(screenFolderInfo.ScreenID, folderInfo.Name);
                    if (refNoteID == null)
                    {
                        // User may have created some folder manually with a name not matching to any record, or record
                        // may have been deleted in Acumatica. We can safely ignore it, but let's write to trace.
                        PXTrace.WriteWarning(String.Format("No record found for folder {0} (screen {1}, ID {2})", folderInfo.Name, screenFolderInfo.ScreenID, folderInfo.ID));
                        continue;
                    }

                    bfc = (BoxFolderCache) this.FoldersByNote.Select(refNoteID);
                    if (bfc != null)
                    {
                        // A folder existed before for this record; clear the previous entry for this refNoteID
                        this.FoldersByNote.Delete(bfc);
                    }

                    // Store folder in cache for future reference
                    bfc = (BoxFolderCache)this.FoldersByFolderID.Cache.CreateInstance();
                    bfc.FolderID = folderInfo.ID;
                    bfc.ParentFolderID = folderInfo.ParentFolderID;
                    bfc.RefNoteID = refNoteID;
                    bfc.LastModifiedDateTime = null; //To force initial sync
                    bfc = this.FoldersByFolderID.Insert(bfc);
                }

                if (bfc.LastModifiedDateTime != folderInfo.ModifiedAt)
                {
                    //The SaveFile call will trigger a Load() on the BoxBlobStorageProvider which can be skipped
                    PXContext.SetSlot<bool>("BoxDisableLoad", true);
                    try
                    {
                        RefreshRecordFileList(screenFolderInfo.ScreenID, folderInfo.Name, folderInfo.ID, bfc.RefNoteID);
                        bfc.LastModifiedDateTime = folderInfo.ModifiedAt;
                        this.FoldersByFolderID.Update(bfc);
                        this.Actions.PressSave();
                    }
                    finally
                    {
                        PXContext.SetSlot<bool>("BoxDisableLoad", false);
                    }
                }
            }
        }

        public void RefreshRecordFileList(string screenID, string folderName, string folderID, Guid? refNoteID)
        {
            var tokenHandler = PXGraph.CreateInstance<UserTokenHandler>();

            //Get list of files contained in the record folder. RecurseDepth=0 will retrieve all subfolders
            List<BoxUtils.FileFolderInfo> boxFileList = BoxUtils.GetFileList(tokenHandler, folderID, 0).Result;

            // Remove any files which were deleted in Box and still exist in the record.
            foreach (PXResult<BoxFileCache, UploadFileRevisionNoData, UploadFile, NoteDoc> result in FilesByNoteID.Select(refNoteID))
            {
                BoxUtils.FileFolderInfo boxFile = boxFileList.FirstOrDefault(f => f.ID == ((BoxFileCache)result).FileID);
                if (boxFile == null)
                {
                    //File was deleted
                    this.FilesByNoteID.Delete(result);
                    this.UploadFiles.Delete(result);
                    this.UploadFileRevisions.Delete(result);
                    this.NoteDocs.Delete(result);
                }
                else
                {
                    // File still exists, remove it from in-memory list 
                    // so we don't process it as a new file in the next loop
                    boxFileList.Remove(boxFile);
                }
            }

            if (boxFileList.Count > 0)
            {
                UploadFileMaintenance ufm = PXGraph.CreateInstance<UploadFileMaintenance>();
                ufm.IgnoreFileRestrictions = true;

                ufm.RowInserting.AddHandler<UploadFileRevision>(delegate (PXCache sender, PXRowInsertingEventArgs e)
                {
                    ((UploadFileRevision)e.Row).BlobHandler = new Guid?(Guid.NewGuid());
                });

                foreach (BoxUtils.FileFolderInfo boxFile in boxFileList)
                {
                    ufm.Clear();
                    string fileName = string.Format("{0}\\{1}", folderName, boxFile.Name);
                    FileInfo fileInfo = ufm.GetFileWithNoData(fileName);
                    Guid? blobHandlerGuid;
                    if (fileInfo == null)
                    {
                        fileInfo = new FileInfo(fileName, null, new byte[0]);
                        if (!ufm.SaveFile(fileInfo)) throw new PXException(Messages.ErrorAddingFileSaveFileFailed, fileName);
                        if (!fileInfo.UID.HasValue) throw new PXException(Messages.ErrorAddingFileUIDNull, fileName);

                        UploadFileMaintenance.SetAccessSource(fileInfo.UID.Value, null, screenID);
                        NoteDoc noteDoc = (NoteDoc)this.NoteDocs.Cache.CreateInstance();
                        noteDoc.NoteID = refNoteID;
                        noteDoc.FileID = fileInfo.UID;
                        this.NoteDocs.Insert(noteDoc);

                        blobHandlerGuid = ufm.Revisions.Current.BlobHandler;
                    }
                    else
                    {
                        //File already exists in the database, retrieve BlobHandler
                        if (!fileInfo.UID.HasValue) throw new PXException(Messages.GetFileWithNoDataReturnedUIDNull, fileName);
                        blobHandlerGuid = GetBlobHandlerForFileID(fileInfo.UID.Value);
                    }

                    var bfc = (BoxFileCache)this.FilesByBlobHandler.Cache.CreateInstance();
                    bfc.BlobHandler = blobHandlerGuid;
                    bfc.FileID = boxFile.ID;
                    bfc.ParentFolderID = boxFile.ParentFolderID;
                    bfc = this.FilesByBlobHandler.Insert(bfc);
                }
            }
        }

        private Guid GetBlobHandlerForFileID(Guid fileID)
        {
            UploadFileRevisionNoData ufr = (UploadFileRevisionNoData)new PXSelect<UploadFileRevisionNoData,
                Where<UploadFileRevisionNoData.fileID, Equal<Required<UploadFileRevisionNoData.fileID>>>,
                OrderBy<Desc<UploadFileRevisionNoData.fileRevisionID>>>(this).Select(fileID);

            if (ufr == null) throw new PXException(Messages.UploadFileRevisionMissing, fileID);
            if (!ufr.BlobHandler.HasValue) throw new PXException(Messages.UploadFileRevisionMissingBlobHandler, fileID);

            return ufr.BlobHandler.Value;
        }

        private Guid? FindMatchingNoteIDForFolder(string screenID, string keyValues)
        {
            string graphType = PXPageIndexingService.GetGraphTypeByScreenID(screenID);
            if (String.IsNullOrEmpty(graphType)) throw new PXException(Messages.PrimaryGraphForScreenIDNotFound, screenID);

            string primaryViewName = PXPageIndexingService.GetPrimaryView(graphType);
            if (String.IsNullOrEmpty(primaryViewName)) throw new PXException(Messages.PrimaryGraphForScreenIDNotFound, graphType);

            // TODO: Verify if this is a problem, normally this is obtained from PXSiteMap.ScreenInfo
            // but this info is slow to load and not always available??? It is only used with ScreenUtils.SelectCurrent
            // for this line: if (primaryViewInfo.Parameters.Any(p => p.Name == pair.Key)) parameters.Add(val);
            var viewDescription = new Data.Description.PXViewDescription(primaryViewName);

            var graph = PXGraph.CreateInstance(PXBuildManager.GetType(graphType, true));
            var keyValuePairs = GetKeyValuePairsFromKeyValues(graph, primaryViewName, keyValues);
            var view = graph.Views[primaryViewName];
            ScreenUtils.SelectCurrent(view, viewDescription, keyValuePairs);

            if (view.Cache.Current == null)
            {
                return null;
            }
            else
            {
                return PXNoteAttribute.GetNoteID(view.Cache, view.Cache.Current, EntityHelper.GetNoteField(view.Cache.Current.GetType()));
            }
        }

        private KeyValuePair<string, string>[] GetKeyValuePairsFromKeyValues(PXGraph graph, string viewName, string keyValues)
        {
            string[] keyNames = graph.GetKeyNames(viewName);
            string[] keyValuesArray = keyValues.Split(' ');

            if (keyNames.Length != keyValuesArray.Length)
                throw new PXException(Messages.ErrorExtractingKeyValuesFromFolderName, keyValuesArray.Length, keyValues.Length, viewName);

            var pairs = new KeyValuePair<string, string>[keyNames.Length];

            for (int i = 0; i < keyNames.Length; i++)
            {
                pairs[i] = new KeyValuePair<string, string>(keyNames[i], keyValuesArray[i]);
            }

            return pairs;
        }

        public BoxUtils.FileFolderInfo GetRootFolder()
        {
            string rootFolderName = GetRootFolderName();
            if (string.IsNullOrEmpty(rootFolderName)) throw new PXException(Messages.RootFolderNotSetup);

            var tokenHandler = PXGraph.CreateInstance<UserTokenHandler>();
            BoxUtils.FileFolderInfo rootFolder = BoxUtils.FindFolder(tokenHandler, "0", rootFolderName).Result;
            if (rootFolder == null) throw new PXException(Messages.RootFolderNotFound, rootFolderName);

            return rootFolder;
        }

        private string GetRootFolderName()
        {
            BlobProviderSettings providerSettings = (BlobProviderSettings)PXSelect<BlobProviderSettings, Where<BlobProviderSettings.name, Equal<Required<BlobProviderSettings.name>>>>.Select(this, BoxBlobStorageProvider.RootFolderParam);
            if (providerSettings == null)
                return string.Empty;
            return providerSettings.Value;
        }
    }
}
