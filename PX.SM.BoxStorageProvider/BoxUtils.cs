﻿using Box.V2;
using Box.V2.Auth;
using Box.V2.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PX.SM.BoxStorageProvider
{
    public static class BoxUtils
    {
        public const string ClientID = "bfqst5k2brzabtaorwrn68y2eqarwmhm";
        private const string ClientSecret = "9wrzqjJGL8Te0YxyJyCf0RPrSmvIufGk";
        private const string RedirectUri = "https://acumatica.velixo.com/Box";

        //System will do paging if folder contains more than 1000 files
        private const int FolderItemsCollectionLimit = 1000;

        private static BoxClient GetNewBoxClient(string accessToken, string refreshToken)
        {
            var config = new BoxConfig(ClientID, ClientSecret, new Uri(RedirectUri));
            OAuthSession session = new OAuthSession(accessToken, refreshToken, 3600, "bearer");
            
            return new BoxClient(config, session);
        }

        public static string CleanFileOrFolderName(string value)
        {
            string text = Regex.Replace(value, @"[\\\/\""\:\<\>\|\*\?]", " ");
            Regex regex = new Regex("[ ]{2,}");
            text = regex.Replace(text, " ");
            return text.Trim();
        }

        public static async Task<OAuthSession> CompleteAuthorization(string authCode)
        {
            var config = new BoxConfig(ClientID, ClientSecret, new Uri(RedirectUri));
            var client = new BoxClient(config);
            return await client.Auth.AuthenticateAsync(authCode);
        }

        public static async Task<Box.V2.Models.BoxUser> GetUserInfo(string accessToken, string refreshToken)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            return await client.UsersManager.GetCurrentUserInformationAsync();
        }

        public static async Task<FileFolderInfo> CreateFolder(string accessToken, string refreshToken, string name, string parentFolderID)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            var folderRequest = new Box.V2.Models.BoxFolderRequest { Name = name, Parent = new Box.V2.Models.BoxRequestEntity { Id = parentFolderID } };
            Box.V2.Models.BoxFolder folder = await client.FoldersManager.CreateAsync(folderRequest, new List<string> { Box.V2.Models.BoxFolder.FieldName, Box.V2.Models.BoxFolder.FieldModifiedAt } );
            return new FileFolderInfo
            {
                ID = folder.Id,
                Name = folder.Name,
                ParentFolderID = parentFolderID,
                ModifiedAt = folder.ModifiedAt
            };
        }

        public static async Task<FileFolderInfo> GetFileInfo(string accessToken, string refreshToken, string fileID)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            Box.V2.Models.BoxFile file = await client.FilesManager.GetInformationAsync(fileID, new List<string> { Box.V2.Models.BoxFile.FieldName, Box.V2.Models.BoxFile.FieldModifiedAt, Box.V2.Models.BoxFile.FieldParent });
            return new FileFolderInfo
            {
                ID = file.Id,
                Name = file.Name,
                ParentFolderID = file.Parent == null ? "0" : file.Parent.Id,
                ModifiedAt = file.ModifiedAt
            };
        }

        public static async Task<FileFolderInfo> GetFolderInfo(string accessToken, string refreshToken, string folderID)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            Box.V2.Models.BoxFolder folder = await client.FoldersManager.GetInformationAsync(folderID, new List<string> { Box.V2.Models.BoxFolder.FieldName, Box.V2.Models.BoxFolder.FieldModifiedAt, Box.V2.Models.BoxFolder.FieldParent });
            return new FileFolderInfo
            {
                ID = folder.Id,
                Name = folder.Name,
                ParentFolderID = folder.Parent == null ? "0" : folder.Parent.Id,
                ModifiedAt = folder.ModifiedAt
            };
        }

        public static async Task<FileFolderInfo> UploadFile(string accessToken, string refreshToken, string parentFolderID, string fileName, byte[] data)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            var fileRequest = new Box.V2.Models.BoxFileRequest { Name = fileName, Parent = new Box.V2.Models.BoxRequestEntity { Id = parentFolderID } };
            Box.V2.Models.BoxFile file = await client.FilesManager.UploadAsync(fileRequest, new MemoryStream(data));
            return new FileFolderInfo
            {
                ID = file.Id,
                Name = file.Name,
                ParentFolderID = parentFolderID
            };
        }

        public static async Task<byte[]> DownloadFile(string accessToken, string refreshToken, string fileID)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            var memoryStream = new MemoryStream();
            using (Stream stream = await client.FilesManager.DownloadStreamAsync(fileID))
            {
                int bytesRead;
                var buffer = new byte[8192];
                do
                {
                    bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    await memoryStream.WriteAsync(buffer, 0, bytesRead);
                } while (bytesRead > 0);
            }
            return memoryStream.ToArray();
        }

        public static async void DeleteFile(string accessToken, string refreshToken, string fileID)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            await client.FilesManager.DeleteAsync(fileID);
        }

        public static async Task<List<FileFolderInfo>> GetFileList(string accessToken, string refreshToken, string folderID, int recurseDepth)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            return await GetFileListInternal(client, folderID, 0, recurseDepth, 0, String.Empty);
        }

        public static async Task<List<FileFolderInfo>> GetFolderList(string accessToken, string refreshToken, string folderID, int recurseDepth)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            return await GetFolderListInternal(client, folderID, 0, recurseDepth, 0, String.Empty);
        }

        public static async Task<FileFolderInfo> FindFolder(string accessToken, string refreshToken, string parentFolderID, string name)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            return await FindFolderInternal(client, parentFolderID, 0, name);
        }

        private static async Task<FileFolderInfo> FindFolderInternal(BoxClient client, string parentFolderID, int offset, string name)
        {
            var list = new List<FileFolderInfo>();
            var folderItems = await client.FoldersManager.GetFolderItemsAsync(parentFolderID, FolderItemsCollectionLimit, offset, new List<string> { Box.V2.Models.BoxFolder.FieldName, Box.V2.Models.BoxFolder.FieldModifiedAt } );

            foreach (var item in folderItems.Entries)
            {
                if (item.Type == "folder" && item.Name == name)
                {
                    return new FileFolderInfo
                    {
                        ID = item.Id,
                        Name = item.Name,
                        ParentFolderID = parentFolderID,
                        ModifiedAt = item.ModifiedAt
                    };
                }
            }

            if (folderItems.Offset + folderItems.Limit < folderItems.TotalCount)
            {
                return await FindFolderInternal(client, parentFolderID, offset + folderItems.Limit, name);
            }

            return null;
        }

        private static async Task<List<FileFolderInfo>> GetFileListInternal(BoxClient client, string folderID, int offset, int recurseDepth, int currentDepth, string levelName)
        {
            var list = new List<FileFolderInfo>();
            var folderItems = await client.FoldersManager.GetFolderItemsAsync(folderID, FolderItemsCollectionLimit, offset);

            foreach (var item in folderItems.Entries)
            {
                if (item.Type == "folder" && (currentDepth < recurseDepth || recurseDepth == 0))
                {
                    list.AddRange(await GetFileListInternal(client, item.Id, 0, recurseDepth, currentDepth + 1, string.IsNullOrEmpty(levelName) ? item.Name : string.Format("{0}\\{1}", levelName, item.Name)));
                }
                else
                {
                    list.Add(new FileFolderInfo
                    {
                        ID = item.Id,
                        ParentFolderID = folderID,
                        Name = string.IsNullOrEmpty(levelName) ? item.Name : string.Format("{0}\\{1}", levelName, item.Name)
                    });
                }
            }

            if (folderItems.Offset + folderItems.Limit < folderItems.TotalCount)
            {
                list.AddRange(await GetFileListInternal(client, folderID, offset + folderItems.Limit, recurseDepth, currentDepth, levelName));
            }

            return list;
        }


        private static async Task<List<FileFolderInfo>> GetFolderListInternal(BoxClient client, string folderID, int offset, int recurseDepth, int currentDepth, string levelName)
        {
            var list = new List<FileFolderInfo>();
            var folderItems = await client.FoldersManager.GetFolderItemsAsync(folderID, FolderItemsCollectionLimit, offset, new List<string> { Box.V2.Models.BoxFolder.FieldName, Box.V2.Models.BoxFolder.FieldModifiedAt });

            foreach (var item in folderItems.Entries)
            {
                if (item.Type == "folder")
                {
                    list.Add(new FileFolderInfo
                    {
                        ID = item.Id,
                        ParentFolderID = folderID,
                        Name = string.IsNullOrEmpty(levelName) ? item.Name : string.Format("{0}\\{1}", levelName, item.Name),
                        ModifiedAt = item.ModifiedAt
                    });

                    if (currentDepth < recurseDepth || recurseDepth == 0)
                    {
                        list.AddRange(await GetFolderListInternal(client, item.Id, 0, recurseDepth, currentDepth + 1, string.IsNullOrEmpty(levelName) ? item.Name : string.Format("{0}\\{1}", levelName, item.Name)));
                    }
                }
            }

            if (folderItems.Offset + folderItems.Limit < folderItems.TotalCount)
            {
                list.AddRange(await GetFolderListInternal(client, folderID, offset + folderItems.Limit, recurseDepth, currentDepth, levelName));
            }

            return list;
        }

        public static async Task SetFileDescription(string accessToken, string refreshToken, string fileID, string description)
        {
            var client = GetNewBoxClient(accessToken, refreshToken);
            var fileRequest = new Box.V2.Models.BoxFileRequest { Id = fileID, Description = description };
            await client.FilesManager.UpdateInformationAsync(fileRequest);
        }

        public class FileFolderInfo
        {
            public string ID;
            public string Name;
            public string ParentFolderID;
            public DateTime? ModifiedAt;
        }
    }
}
