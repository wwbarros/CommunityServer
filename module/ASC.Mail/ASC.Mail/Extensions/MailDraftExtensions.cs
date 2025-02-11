/*
 *
 * (c) Copyright Ascensio System Limited 2010-2021
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * http://www.apache.org/licenses/LICENSE-2.0
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web;

using ASC.Common.Logging;
using ASC.Files.Core;
using ASC.Mail.Data.Contracts;
using ASC.Mail.Data.Contracts.Base;
using ASC.Mail.Data.Storage;
using ASC.Mail.Exceptions;
using ASC.Mail.Utils;
using ASC.Web.Files.Services.WCFService;

using HtmlAgilityPack;

using MimeKit;

namespace ASC.Mail.Extensions
{
    public static class MailDraftExtensions
    {
        public static MailMessageData ToMailMessage(this MailComposeBase draft)
        {
            MailboxAddress fromVerified;

            if (string.IsNullOrEmpty(draft.From))
                throw new DraftException(DraftException.ErrorTypes.EmptyField, "Empty email address in {0} field",
                    DraftFieldTypes.From);

            if (!MailboxAddress.TryParse(ParserOptions.Default, draft.From, out fromVerified))
                throw new DraftException(DraftException.ErrorTypes.IncorrectField, "Incorrect email address",
                    DraftFieldTypes.From);

            if (string.IsNullOrEmpty(fromVerified.Name))
                fromVerified.Name = draft.Mailbox.Name;

            if (string.IsNullOrEmpty(draft.MimeMessageId))
                throw new ArgumentException("MimeMessageId");

            var messageItem = new MailMessageData
            {
                From = fromVerified.ToString(),
                FromEmail = fromVerified.Address,
                To = string.Join(", ", draft.To.ToArray()),
                Cc = draft.Cc != null ? string.Join(", ", draft.Cc.ToArray()) : "",
                Bcc = draft.Bcc != null ? string.Join(", ", draft.Bcc.ToArray()) : "",
                Subject = draft.Subject,
                Date = DateTime.UtcNow,
                Important = draft.Important,
                HtmlBody = draft.HtmlBody,
                Introduction = MailUtil.GetIntroduction(draft.HtmlBody),
                StreamId = draft.StreamId,
                TagIds = draft.Labels != null && draft.Labels.Count != 0 ? new List<int>(draft.Labels) : null,
                Size = draft.HtmlBody.Length,
                MimeReplyToId = draft.MimeReplyToId,
                MimeMessageId = draft.MimeMessageId,
                IsNew = false,
                Folder = draft.Folder,
                ChainId = draft.MimeMessageId,
                CalendarUid = draft.CalendarEventUid,
                CalendarEventIcs = draft.CalendarIcs,
                MailboxId = draft.Mailbox.MailBoxId
            };

            if (messageItem.Attachments == null)
            {
                messageItem.Attachments = new List<MailAttachmentData>();
            }

            draft.Attachments.ForEach(attachment =>
            {
                attachment.tenant = draft.Mailbox.TenantId;
                attachment.user = draft.Mailbox.UserId;
            });

            messageItem.Attachments.AddRange(draft.Attachments);

            messageItem.HasAttachments = messageItem.Attachments.Count > 0;

            return messageItem;
        }

        public static MimeMessage ToMimeMessage(this MailDraftData draft)
        {
            var mimeMessage = new MimeMessage
            {
                Date = DateTime.UtcNow,
                Subject = !string.IsNullOrEmpty(draft.Subject) ? draft.Subject : "",
                MessageId = draft.MimeMessageId
            };

            var from = MailboxAddress.Parse(ParserOptions.Default, draft.From);

            mimeMessage.From.Add(from);

            if (draft.To.Any())
                mimeMessage.To.AddRange(draft.To.ConvertAll(MailboxAddress.Parse));

            if (draft.Cc.Any())
                mimeMessage.Cc.AddRange(draft.Cc.ConvertAll(MailboxAddress.Parse));

            if (draft.Bcc.Any())
                mimeMessage.Bcc.AddRange(draft.Bcc.ConvertAll(MailboxAddress.Parse));

            if (draft.Important)
            {
                mimeMessage.Importance = MessageImportance.High;
                mimeMessage.XPriority = XMessagePriority.Highest;
            }

            if (!string.IsNullOrEmpty(draft.MimeReplyToId))
                mimeMessage.InReplyTo = draft.MimeReplyToId;

            mimeMessage.Body = ToMimeMessageBody(draft);

            if (draft.IsAutogenerated)
            {
                mimeMessage.Headers.Add("Auto-Submitted", "auto-generated");
            }

            if (draft.IsAutoreplied)
            {
                mimeMessage.Headers.Add("Auto-Submitted", "auto-replied");
            }

            if (draft.RequestReceipt)
            {
                mimeMessage.Headers[HeaderId.ReturnReceiptTo] = from.ToString(true);
            }

            if (draft.RequestRead)
            {
                mimeMessage.Headers[HeaderId.DispositionNotificationTo] = from.ToString(true);
            }

            return mimeMessage;
        }

        private static MimePart ConvertToMimePart(MailAttachmentData mailAttachmentData, string contentId = null)
        {
            var contentType = ContentType.Parse(
                !string.IsNullOrEmpty(mailAttachmentData.contentType)
                    ? mailAttachmentData.contentType
                    : MimeMapping.GetMimeMapping(mailAttachmentData.fileName));

            var mimePart = new MimePart(contentType)
            {
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = mailAttachmentData.fileName
            };

            if (string.IsNullOrEmpty(contentId))
            {
                mimePart.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment);
            }
            else
            {
                mimePart.ContentDisposition = new ContentDisposition(ContentDisposition.Inline);
                mimePart.ContentId = contentId;
                mimePart.ContentType.Name = mailAttachmentData.fileName;
            }

            MemoryStream ms;

            if (mailAttachmentData.data == null)
            {
                var s3Key = MailStoragePathCombiner.GerStoredFilePath(mailAttachmentData);

                ms = new MemoryStream();

                using (var stream = StorageManager
                    .GetDataStoreForAttachments(mailAttachmentData.tenant)
                    .GetReadStream(s3Key))
                {
                    stream.CopyTo(ms);
                }
            }
            else
            {
                ms = new MemoryStream(mailAttachmentData.data);
            }

            mimePart.Content = new MimeContent(ms);

            Parameter param;

            if (mimePart.ContentDisposition != null && mimePart.ContentDisposition.Parameters.TryGetValue("filename", out param))
                param.EncodingMethod = ParameterEncodingMethod.Rfc2047;

            if (mimePart.ContentType.Parameters.TryGetValue("name", out param))
                param.EncodingMethod = ParameterEncodingMethod.Rfc2047;

            return mimePart;
        }

        private static MimeEntity ToMimeMessageBody(MailDraftData draft)
        {
            string textBody;
            MailUtil.TryExtractTextFromHtml(draft.HtmlBody, out textBody);

            MultipartAlternative alternative = null;
            MimeEntity body = null;

            if (!string.IsNullOrEmpty(textBody))
            {
                var textPart = new TextPart("plain")
                {
                    Text = textBody,
                    ContentTransferEncoding = ContentEncoding.QuotedPrintable
                };

                if (!string.IsNullOrEmpty(draft.HtmlBody))
                {
                    alternative = new MultipartAlternative { textPart };
                    body = alternative;
                }
                else
                    body = textPart;
            }

            if (!string.IsNullOrEmpty(draft.HtmlBody))
            {
                var htmlPart = new TextPart("html")
                {
                    Text = draft.HtmlBody,
                    ContentTransferEncoding = ContentEncoding.QuotedPrintable
                };

                MimeEntity html;

                if (draft.AttachmentsEmbedded.Any())
                {
                    htmlPart.ContentTransferEncoding = ContentEncoding.Base64;

                    var related = new MultipartRelated
                    {
                        Root = htmlPart
                    };

                    related.Root.ContentId = null;

                    foreach (var emb in draft.AttachmentsEmbedded)
                    {
                        var linkedResource = ConvertToMimePart(emb, emb.contentId);
                        related.Add(linkedResource);
                    }

                    html = related;
                }
                else
                    html = htmlPart;

                if (alternative != null)
                    alternative.Add(html);
                else
                    body = html;
            }

            if (!string.IsNullOrEmpty(draft.CalendarIcs))
            {
                var calendarPart = new TextPart("calendar")
                {
                    Text = draft.CalendarIcs,
                    ContentTransferEncoding = ContentEncoding.QuotedPrintable
                };

                calendarPart.ContentType.Parameters.Add("method", draft.CalendarMethod);

                if (alternative != null)
                    alternative.Add(calendarPart);
                else
                    body = calendarPart;
            }


            if (draft.Attachments.Any() || !string.IsNullOrEmpty(draft.CalendarIcs))
            {
                var mixed = new Multipart("mixed");

                if (body != null)
                    mixed.Add(body);

                foreach (var att in draft.Attachments)
                {
                    var attachment = ConvertToMimePart(att);
                    mixed.Add(attachment);
                }

                if (!string.IsNullOrEmpty(draft.CalendarIcs))
                {
                    var filename = "calendar.ics";
                    switch (draft.CalendarMethod)
                    {
                        case Defines.ICAL_REQUEST:
                            filename = "invite.ics";
                            break;
                        case Defines.ICAL_REPLY:
                            filename = "reply.ics";
                            break;
                        case Defines.ICAL_CANCEL:
                            filename = "cancel.ics";
                            break;
                    }

                    var contentType = new ContentType("application", "ics");
                    contentType.Parameters.Add("method", draft.CalendarMethod);
                    contentType.Parameters.Add("name", filename);

                    var calendarResource = new MimePart(contentType)
                    {
                        ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                        ContentTransferEncoding = ContentEncoding.Base64,
                        FileName = filename
                    };

                    var data = Encoding.UTF8.GetBytes(draft.CalendarIcs);

                    var ms = new MemoryStream(data);

                    calendarResource.Content = new MimeContent(ms);

                    mixed.Add(calendarResource);
                }

                body = mixed;
            }

            if (body != null)
                return body;

            return new TextPart("plain")
            {
                Text = string.Empty
            };
        }

        public static void ChangeAttachedFileLinksAddresses(this MailDraftData draft, ILog log = null)
        {
            if (log == null)
                log = new NullLog();

            var doc = new HtmlDocument();
            doc.LoadHtml(draft.HtmlBody);

            var linkNodes = doc.DocumentNode.SelectNodes("//a[contains(@class,'mailmessage-filelink-link')]");
            if (linkNodes == null) return;

            var fileStorageService = new FileStorageServiceController();

            var setLinks = new List<Tuple<string, string>>();
            foreach (var linkNode in linkNodes)
            {
                var fileId = linkNode.Attributes["data-fileid"].Value;
                var objectId = "file_" + fileId;

                linkNode.Attributes["class"].Remove(); // 'mailmessage-filelink-link'
                linkNode.Attributes["data-fileid"].Remove(); // 'data-fileid'

                var setLink = setLinks.SingleOrDefault(x => x.Item1 == fileId);
                if (setLink != null)
                {
                    linkNode.SetAttributeValue("href", setLink.Item2);
                    log.InfoFormat("ChangeAttachedFileLinks() Change file link href: {0}", fileId);
                    continue;
                }

                var aceCollection = new AceCollection
                {
                    Entries = new ItemList<string> { objectId },
                    Aces = new ItemList<AceWrapper>
                            {
                                new AceWrapper
                                    {
                                        SubjectId = FileConstant.ShareLinkId,
                                        SubjectGroup = true,
                                        Share = draft.FileLinksShareMode
                                    }
                            }
                };

                fileStorageService.SetAceObject(aceCollection, false);
                log.InfoFormat("ChangeAttachedFileLinks() Set public accees to file: {0}", fileId);
                var sharedInfo =
                    fileStorageService.GetSharedInfo(new ItemList<string> { objectId })
                                      .Find(r => r.SubjectId == FileConstant.ShareLinkId);
                linkNode.SetAttributeValue("href", sharedInfo.Link);
                log.InfoFormat("ChangeAttachedFileLinks() Change file link href: {0}", fileId);
                setLinks.Add(new Tuple<string, string>(fileId, sharedInfo.Link));
            }

            linkNodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'mailmessage-filelink')]");
            foreach (var linkNode in linkNodes)
            {
                linkNode.Attributes["class"].Remove();
            }

            draft.HtmlBody = doc.DocumentNode.OuterHtml;
        }

        public static List<string> GetEmbeddedAttachmentLinks(this MailComposeBase draft)
        {
            var links = new List<string>();

            var fckStorage = StorageManager.GetDataStoreForCkImages(draft.Mailbox.TenantId);
            //todo: replace selector
            var currentMailFckeditorUrl = fckStorage.GetUri(StorageManager.CKEDITOR_IMAGES_DOMAIN, "").ToString();
            var currentMailAttachmentFolderUrl = MailStoragePathCombiner.GetMessageDirectory(draft.Mailbox.UserId,
                draft.StreamId);
            var currentUserStorageUrl = MailStoragePathCombiner.GetUserMailsDirectory(draft.Mailbox.UserId);
            var xpathQuery = StorageManager.GetXpathQueryForAttachmentsToResaving(currentMailFckeditorUrl,
                currentMailAttachmentFolderUrl,
                currentUserStorageUrl);

            var doc = new HtmlDocument();
            doc.LoadHtml(draft.HtmlBody);

            var linkNodes = doc.DocumentNode.SelectNodes(xpathQuery);

            if (linkNodes == null)
                return links;

            links.AddRange(linkNodes.Select(linkNode => linkNode.Attributes["src"].Value));

            return links;
        }

        public static void ChangeEmbeddedAttachmentLinks(this MailDraftData draft, ILog log = null)
        {
            if (log == null)
                log = new NullLog();

            var baseAttachmentFolder = MailStoragePathCombiner.GetMessageDirectory(draft.Mailbox.UserId, draft.StreamId);

            var doc = new HtmlDocument();
            doc.LoadHtml(draft.HtmlBody);
            var linkNodes = doc.DocumentNode.SelectNodes("//img[@src and (contains(@src,'" + baseAttachmentFolder + "') or @x-mail-embedded)]");
            if (linkNodes == null) return;

            foreach (var linkNode in linkNodes)
            {
                var link = linkNode.Attributes["src"].Value;
                log.InfoFormat("ChangeEmbededAttachmentLinks() Embeded attachment link for changing to cid: {0}", link);

                var isExternal = link.IndexOf(baseAttachmentFolder) == -1;

                var fileLink = isExternal
                    ? link
                    : HttpUtility.UrlDecode(link.Substring(baseAttachmentFolder.Length));
                var fileName = Path.GetFileName(fileLink);

                var attach = CreateEmbbededAttachment(fileName, link, fileLink, draft.Mailbox.UserId, draft.Mailbox.TenantId, draft.Mailbox.MailBoxId, draft.StreamId);

                if (isExternal)
                {
                    using (var webClient = new WebClient())
                    {
                        //webClient.Headers.Add("Authorization", GetPartnerAuthHeader(actionUrl));
                        try
                        {
                            attach.data = webClient.DownloadData(fileLink);
                        }
                        catch (WebException we)
                        {
                            log.Error(we);
                            continue;
                        }
                    }
                }
                draft.AttachmentsEmbedded.Add(attach);
                linkNode.SetAttributeValue("src", "cid:" + attach.contentId);
                log.InfoFormat("ChangeEmbededAttachmentLinks() Attachment cid: {0}", attach.contentId);
            }
            draft.HtmlBody = doc.DocumentNode.OuterHtml;
        }

        public static void ChangeSmileLinks(this MailDraftData draft, ILog log = null)
        {
            if (log == null)
                log = new NullLog();

            var baseSmileUrl = MailStoragePathCombiner.GetEditorSmileBaseUrl();

            var doc = new HtmlDocument();
            doc.LoadHtml(draft.HtmlBody);
            var linkNodes = doc.DocumentNode.SelectNodes("//img[@src and (contains(@src,'" + baseSmileUrl + "'))]");
            if (linkNodes == null) return;

            foreach (var linkNode in linkNodes)
            {
                var link = linkNode.Attributes["src"].Value;

                log.InfoFormat("ChangeSmileLinks() Link to smile: {0}", link);

                var fileName = Path.GetFileName(link);

                var data = StorageManager.LoadLinkData(link, log);

                if (!data.Any())
                    continue;

                var attach = new MailAttachmentData
                {
                    fileName = fileName,
                    storedName = fileName,
                    contentId = link.GetMd5(),
                    data = data
                };

                log.InfoFormat("ChangeSmileLinks() Embedded smile contentId: {0}", attach.contentId);

                linkNode.SetAttributeValue("src", "cid:" + attach.contentId);

                if (draft.AttachmentsEmbedded.All(x => x.contentId != attach.contentId))
                {
                    draft.AttachmentsEmbedded.Add(attach);
                }
            }
            draft.HtmlBody = doc.DocumentNode.OuterHtml;
        }

        public static void ChangeUrlProxyLinks(this MailDraftData draft, ILog log = null)
        {
            if (log == null)
                log = new NullLog();

            try
            {
                draft.HtmlBody = HtmlSanitizer.RemoveProxyHttpUrls(draft.HtmlBody);
            }
            catch (Exception ex)
            {
                log.ErrorFormat("ChangeUrlProxyLinks(): Exception: {0}", ex.ToString());
            }
        }

        public static void ChangeAttachedFileLinksImages(this MailDraftData draft, ILog log = null)
        {
            if (log == null)
                log = new NullLog();

            var baseSmileUrl = MailStoragePathCombiner.GetEditorImagesBaseUrl();

            var doc = new HtmlDocument();
            doc.LoadHtml(draft.HtmlBody);
            var linkNodes = doc.DocumentNode.SelectNodes("//img[@src and (contains(@src,'" + baseSmileUrl + "'))]");
            if (linkNodes == null) return;

            foreach (var linkNode in linkNodes)
            {
                var link = linkNode.Attributes["src"].Value;
                log.InfoFormat("ChangeAttachedFileLinksImages() Link to file link: {0}", link);

                var fileName = Path.GetFileName(link);

                var data = StorageManager.LoadLinkData(link, log);

                if (!data.Any())
                    continue;

                var attach = new MailAttachmentData
                {
                    fileName = fileName,
                    storedName = fileName,
                    contentId = link.GetMd5(),
                    data = data
                };

                log.InfoFormat("ChangeAttachedFileLinksImages() Embedded file link contentId: {0}", attach.contentId);
                linkNode.SetAttributeValue("src", "cid:" + attach.contentId);

                if (draft.AttachmentsEmbedded.All(x => x.contentId != attach.contentId))
                {
                    draft.AttachmentsEmbedded.Add(attach);
                }
            }

            draft.HtmlBody = doc.DocumentNode.OuterHtml;
        }

        public static void ChangeAllImagesLinksToEmbedded(this MailDraftData draft, ILog log = null)
        {
            if (log == null)
                log = new NullLog();

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(draft.HtmlBody);
                var linkNodes = doc.DocumentNode.SelectNodes("//img[@src]");
                if (linkNodes == null) return;

                foreach (var linkNode in linkNodes)
                {
                    var link = linkNode.Attributes["src"].Value;
                    log.InfoFormat("ChangeAllImagesLinksToEmbedded() Link to img link: {0}", link);

                    var fileName = Path.GetFileName(link);

                    var data = StorageManager.LoadLinkData(link, log);

                    if (!data.Any())
                        continue;

                    var attach = new MailAttachmentData
                    {
                        fileName = fileName,
                        storedName = fileName,
                        contentId = link.GetMd5(),
                        data = data
                    };

                    log.InfoFormat("ChangeAllImagesLinksToEmbedded() Embedded img link contentId: {0}", attach.contentId);
                    linkNode.SetAttributeValue("src", "cid:" + attach.contentId);

                    if (draft.AttachmentsEmbedded.All(x => x.contentId != attach.contentId))
                    {
                        draft.AttachmentsEmbedded.Add(attach);
                    }
                }

                draft.HtmlBody = doc.DocumentNode.OuterHtml;
            }
            catch (Exception ex)
            {
                log.ErrorFormat("ChangeAllImagesLinksToEmbedded(): Exception: {0}", ex.ToString());
            }
        }

        private static MailAttachmentData CreateEmbbededAttachment(string fileName, string link, string fileLink, string user,
                                                        int tenant, int mailboxId, string streamId)
        {
            return new MailAttachmentData
            {
                fileName = fileName,
                storedName = fileName,
                contentId = link.GetMd5(),
                storedFileUrl = fileLink,
                streamId = streamId,
                user = user,
                tenant = tenant,
                mailboxId = mailboxId
            };
        }
    }
}
