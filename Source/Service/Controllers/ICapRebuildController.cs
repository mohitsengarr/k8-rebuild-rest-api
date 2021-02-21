using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Glasswall.CloudSdk.AWS.Rebuild.Models;
using Glasswall.CloudSdk.AWS.Rebuild.Services;
using Glasswall.CloudSdk.Common;
using Glasswall.CloudSdk.Common.Web.Abstraction;
using Glasswall.CloudSdk.Common.Web.Models;
using Glasswall.Core.Engine.Common.FileProcessing;
using Glasswall.Core.Engine.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Glasswall.CloudSdk.AWS.Rebuild.Controllers
{
    public class ICapRebuildController : CloudSdkController<ICapRebuildController>
    {
        private readonly IGlasswallVersionService _glasswallVersionService;
        private readonly IFileTypeDetector _fileTypeDetector;
        private readonly IFileProtector _fileProtector;
        private readonly IWebHostEnvironment _hostingEnvironment;
        private readonly IZipUtility _zipUtility;

        public ICapRebuildController(
            IGlasswallVersionService glasswallVersionService,
            IFileTypeDetector fileTypeDetector,
            IFileProtector fileProtector,
            IMetricService metricService,
            ILogger<ICapRebuildController> logger,
            IWebHostEnvironment hostingEnvironment,
            IZipUtility zipUtility) : base(logger, metricService)
        {
            _glasswallVersionService = glasswallVersionService ?? throw new ArgumentNullException(nameof(glasswallVersionService));
            _fileTypeDetector = fileTypeDetector ?? throw new ArgumentNullException(nameof(fileTypeDetector));
            _fileProtector = fileProtector ?? throw new ArgumentNullException(nameof(fileProtector));
            _hostingEnvironment = hostingEnvironment ?? throw new ArgumentNullException(nameof(hostingEnvironment));
            _zipUtility = zipUtility ?? throw new ArgumentNullException(nameof(zipUtility));
        }

        [HttpPost("file")]
        public async Task<IActionResult> RebuildFromFormFile([FromForm][Required] IFormFile file)
        {
            string uploads = Path.Combine(_hostingEnvironment.ContentRootPath, Constants.UPLOADS_FOLDER);
            string tempFolderPath = Path.Combine(uploads, Guid.NewGuid().ToString());

            try
            {
                Logger.LogInformation("'{0}' method invoked", nameof(RebuildFromFormFile));

                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryReadFormFile(file, out byte[] fileBytes))
                    return BadRequest("Input file could not be read.");

                string folderName = $"{Guid.NewGuid()}";
                string protectedFolderPath = Path.Combine(tempFolderPath, Guid.NewGuid().ToString());
                string folderPath = Path.Combine(tempFolderPath, folderName);
                string filePath = Path.Combine(folderPath, file.FileName ?? "Unknown");
                if (!Directory.Exists(uploads))
                {
                    Directory.CreateDirectory(uploads);
                }

                if (!Directory.Exists(tempFolderPath))
                {
                    Directory.CreateDirectory(tempFolderPath);
                }

                if (!Directory.Exists(protectedFolderPath))
                {
                    Directory.CreateDirectory(protectedFolderPath);
                }

                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }

                using (Stream fileStream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(fileStream);
                }

                List<IFileProtectResponse> processDirectoryResp = await ProcessDirectory(folderPath, protectedFolderPath);
                IFileProcessStatus fileProcessStatus = processDirectoryResp.Cast<IFileProcessStatus>().FirstOrDefault();
                if (fileProcessStatus == null || !string.IsNullOrWhiteSpace(fileProcessStatus.ErrorMessage))
                {
                    return BadRequest("Input file could not be read.");
                }

                return new FileContentResult(fileProcessStatus.ProtectedFile, "application/octet-stream") { FileDownloadName = file.FileName ?? "Unknown" };
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Exception occured processing file: {e.Message}");
                throw;
            }
            finally
            {
                if (Directory.Exists(tempFolderPath))
                    Directory.Delete(tempFolderPath, true);
            }
        }

        [HttpPost("zipfile")]
        public async Task<IActionResult> RebuildFromFormZipFile([FromForm][Required] IFormFile file)
        {
            string uploads = Path.Combine(_hostingEnvironment.ContentRootPath, Constants.UPLOADS_FOLDER);
            string tempFolderPath = Path.Combine(uploads, Guid.NewGuid().ToString());

            try
            {
                Logger.LogInformation("'{0}' method invoked", nameof(RebuildFromFormZipFile));

                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryReadFormFile(file, out byte[] fileBytes))
                    return BadRequest("Input file could not be read.");

                string zipFolderName = $"{Guid.NewGuid()}";
                string protectedZipFolderPath = Path.Combine(tempFolderPath, Guid.NewGuid().ToString());
                string zipFolderPath = Path.Combine(tempFolderPath, zipFolderName);
                string zipFilePath = $"{zipFolderPath}.{file.FileName ?? "Unknown"}";
                if (!Directory.Exists(uploads))
                {
                    Directory.CreateDirectory(uploads);
                }

                if (!Directory.Exists(tempFolderPath))
                {
                    Directory.CreateDirectory(tempFolderPath);
                }

                if (!Directory.Exists(protectedZipFolderPath))
                {
                    Directory.CreateDirectory(protectedZipFolderPath);
                }

                using (Stream fileStream = new FileStream(zipFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(fileStream);
                }

                _zipUtility.ExtractZipFile(zipFilePath, null, zipFolderPath);
                List<IFileProtectResponse> processDirectoryResp = await ProcessDirectory(zipFolderPath, protectedZipFolderPath);
                string statusMessage = string.Empty;
                processDirectoryResp.Cast<IFileProcessStatus>().ToList().ForEach(x =>
                {
                    if (!string.IsNullOrWhiteSpace(x.ErrorMessage))
                    {
                        statusMessage += $"An error {x.ErrorMessage} occurred while processing the file {x.FileName}{Environment.NewLine}";
                    }
                    else
                    {
                        statusMessage += $"File {x.FileName} is successfully processed.{Environment.NewLine}";
                    }

                    using StreamWriter sw = System.IO.File.CreateText(Path.Combine(protectedZipFolderPath, Constants.STATUS_FILE));
                    sw.WriteLine(statusMessage);
                });

                if (processDirectoryResp.All(x => !string.IsNullOrEmpty(x.ErrorMessage)))
                {
                    return BadRequest("Input file could not be read.");
                }

                _zipUtility.CreateZipFile($"{protectedZipFolderPath}.{FileType.Zip}", null, protectedZipFolderPath);
                byte[] protectedZipBytes = System.IO.File.ReadAllBytes($"{protectedZipFolderPath}.{FileType.Zip}");
                return new FileContentResult(protectedZipBytes, "application/octet-stream") { FileDownloadName = file.FileName ?? "Unknown" };
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Exception occured processing file: {e.Message}");
                throw;
            }
            finally
            {
                if (Directory.Exists(tempFolderPath))
                    Directory.Delete(tempFolderPath, true);
            }
        }

        [HttpPost("s3tozip")]
        public async Task<IActionResult> RebuildFromFormS3ToZipFile([FromForm][Required] string presignedURL)
        {
            string uploads = Path.Combine(_hostingEnvironment.ContentRootPath, Constants.UPLOADS_FOLDER);
            string tempFolderPath = Path.Combine(uploads, Guid.NewGuid().ToString());
            try
            {
                Logger.LogInformation("'{0}' method invoked", nameof(RebuildFromFormS3ToZipFile));

                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                AmazonS3Client amazonS3Client = new AmazonS3Client(Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AWS_ACCESS_KEY_ID),
                                                                   Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AWS_SECRET_ACCESS_KEY),
                                                                   RegionEndpoint.EUWest1);

                AmazonS3Uri amazonS3Uri = new AmazonS3Uri(presignedURL);
                GetObjectRequest request = new GetObjectRequest()
                {
                    BucketName = amazonS3Uri.Bucket,
                    Key = amazonS3Uri.Key
                };

                GetObjectResponse s3objectResponse = await amazonS3Client.GetObjectAsync(request);

                MemoryStream memStream = new MemoryStream();
                s3objectResponse.ResponseStream.CopyTo(memStream);
                memStream.Seek(0, SeekOrigin.Begin);
                IFormFile file = new FormFile(memStream, 0, memStream.Length, null, Path.GetFileName(amazonS3Uri.Key));
                if (!TryReadFormFile(file, out byte[] fileBytes))
                    return BadRequest("Input file could not be read.");

                string zipFolderName = $"{Guid.NewGuid()}";
                string protectedZipFolderPath = Path.Combine(tempFolderPath, Guid.NewGuid().ToString());
                string zipFolderPath = Path.Combine(tempFolderPath, zipFolderName);
                string zipFilePath = $"{zipFolderPath}.{file.FileName ?? "Unknown"}";
                if (!Directory.Exists(uploads))
                {
                    Directory.CreateDirectory(uploads);
                }

                if (!Directory.Exists(tempFolderPath))
                {
                    Directory.CreateDirectory(tempFolderPath);
                }

                if (!Directory.Exists(protectedZipFolderPath))
                {
                    Directory.CreateDirectory(protectedZipFolderPath);
                }

                using (Stream fileStream = new FileStream(zipFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(fileStream);
                }

                _zipUtility.ExtractZipFile(zipFilePath, null, zipFolderPath);
                List<IFileProtectResponse> processDirectoryResp = await ProcessDirectory(zipFolderPath, protectedZipFolderPath);
                string statusMessage = string.Empty;
                processDirectoryResp.Cast<IFileProcessStatus>().ToList().ForEach(x =>
                {
                    if (!string.IsNullOrWhiteSpace(x.ErrorMessage))
                    {
                        statusMessage += $"An error {x.ErrorMessage} occurred while processing the file {x.FileName}{Environment.NewLine}";
                    }
                    else
                    {
                        statusMessage += $"File {x.FileName} is successfully processed.{Environment.NewLine}";
                    }

                    using StreamWriter sw = System.IO.File.CreateText(Path.Combine(protectedZipFolderPath, Constants.STATUS_FILE));
                    sw.WriteLine(statusMessage);
                });

                if (processDirectoryResp.All(x => !string.IsNullOrEmpty(x.ErrorMessage)))
                {
                    return BadRequest("Input file could not be read.");
                }

                _zipUtility.CreateZipFile($"{protectedZipFolderPath}.{FileType.Zip}", null, protectedZipFolderPath);
                byte[] protectedZipBytes = System.IO.File.ReadAllBytes($"{protectedZipFolderPath}.{FileType.Zip}");
                await memStream.DisposeAsync();
                return new FileContentResult(protectedZipBytes, "application/octet-stream") { FileDownloadName = file.FileName ?? "Unknown" };
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Exception occured processing file: {e.Message}");
                throw;
            }
            finally
            {
                if (Directory.Exists(tempFolderPath))
                    Directory.Delete(tempFolderPath, true);
            }
        }

        [HttpPost("s3tos3")]
        public async Task<IActionResult> RebuildFromFormS3ToS3([FromForm][Required] string sourcePresignedURL, [FromForm][Required] string targetPresignedURL)
        {
            string uploads = Path.Combine(_hostingEnvironment.ContentRootPath, Constants.UPLOADS_FOLDER);
            string tempFolderPath = Path.Combine(uploads, Guid.NewGuid().ToString());

            try
            {
                Logger.LogInformation("'{0}' method invoked", nameof(RebuildFromFormS3ToS3));

                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                AmazonS3Client amazonS3Client = new AmazonS3Client(Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AWS_ACCESS_KEY_ID),
                                                                   Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AWS_SECRET_ACCESS_KEY),
                                                                   RegionEndpoint.EUWest1);
                AmazonS3Uri amazonS3Uri = new AmazonS3Uri(sourcePresignedURL);
                GetObjectRequest request = new GetObjectRequest()
                {
                    BucketName = amazonS3Uri.Bucket,
                    Key = amazonS3Uri.Key
                };

                GetObjectResponse s3objectResponse = await amazonS3Client.GetObjectAsync(request);

                MemoryStream memStream = new MemoryStream();
                s3objectResponse.ResponseStream.CopyTo(memStream);
                memStream.Seek(0, SeekOrigin.Begin);
                IFormFile file = new FormFile(memStream, 0, memStream.Length, null, Path.GetFileName(amazonS3Uri.Key));
                if (!TryReadFormFile(file, out byte[] fileBytes))
                    return BadRequest("Input file could not be read.");

                string zipFolderName = $"{Guid.NewGuid()}";
                string protectedZipFolderPath = Path.Combine(tempFolderPath, Guid.NewGuid().ToString());
                string zipFolderPath = Path.Combine(tempFolderPath, zipFolderName);
                string zipFilePath = $"{zipFolderPath}.{file.FileName ?? "Unknown"}";
                if (!Directory.Exists(uploads))
                {
                    Directory.CreateDirectory(uploads);
                }

                if (!Directory.Exists(tempFolderPath))
                {
                    Directory.CreateDirectory(tempFolderPath);
                }

                if (!Directory.Exists(protectedZipFolderPath))
                {
                    Directory.CreateDirectory(protectedZipFolderPath);
                }

                using (Stream fileStream = new FileStream(zipFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(fileStream);
                }

                _zipUtility.ExtractZipFile(zipFilePath, null, zipFolderPath);
                List<IFileProtectResponse> processDirectoryResp = await ProcessDirectory(zipFolderPath, protectedZipFolderPath);
                string statusMessage = string.Empty;
                processDirectoryResp.Cast<IFileProcessStatus>().ToList().ForEach(x =>
                {
                    if (!string.IsNullOrWhiteSpace(x.ErrorMessage))
                    {
                        statusMessage += $"An error {x.ErrorMessage} occurred while processing the file {x.FileName}{Environment.NewLine}";
                    }
                    else
                    {
                        statusMessage += $"File {x.FileName} is successfully processed.{Environment.NewLine}";
                    }

                    using StreamWriter sw = System.IO.File.CreateText(Path.Combine(protectedZipFolderPath, Constants.STATUS_FILE));
                    sw.WriteLine(statusMessage);
                });

                if (processDirectoryResp.All(x => !string.IsNullOrEmpty(x.ErrorMessage)))
                {
                    return BadRequest("Input file could not be read.");
                }

                _zipUtility.CreateZipFile($"{protectedZipFolderPath}.{FileType.Zip}", null, protectedZipFolderPath);
                using (Stream fs = System.IO.File.OpenRead($"{protectedZipFolderPath}.{FileType.Zip}"))
                {
                    AmazonS3Uri amazonS3TargetUri = new AmazonS3Uri(targetPresignedURL);
                    PutObjectRequest putRequest = new PutObjectRequest()
                    {
                        InputStream = fs,
                        BucketName = amazonS3TargetUri.Bucket,
                        Key = amazonS3TargetUri.Key
                    };

                    PutObjectResponse response = await amazonS3Client.PutObjectAsync(putRequest);
                    await memStream.DisposeAsync();
                    if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
                    {
                        return BadRequest("S3 target updation failed.");
                    }
                }

                return Ok();
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Exception occured processing file: {e.Message}");
                throw;
            }
            finally
            {
                if (Directory.Exists(tempFolderPath))
                    Directory.Delete(tempFolderPath, true);
            }
        }

        [HttpPost("ziptos3")]
        public async Task<IActionResult> RebuildFromFormZipFileToS3([FromForm][Required] IFormFile file, [FromForm][Required] string targetPresignedURL)
        {
            string uploads = Path.Combine(_hostingEnvironment.ContentRootPath, Constants.UPLOADS_FOLDER);
            string tempFolderPath = Path.Combine(uploads, Guid.NewGuid().ToString());

            try
            {
                Logger.LogInformation("'{0}' method invoked", nameof(RebuildFromFormZipFileToS3));

                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryReadFormFile(file, out byte[] fileBytes))
                    return BadRequest("Input file could not be read.");

                string zipFolderName = $"{Guid.NewGuid()}";
                string protectedZipFolderPath = Path.Combine(tempFolderPath, Guid.NewGuid().ToString());
                string zipFolderPath = Path.Combine(tempFolderPath, zipFolderName);
                string zipFilePath = $"{zipFolderPath}.{file.FileName ?? "Unknown"}";
                if (!Directory.Exists(uploads))
                {
                    Directory.CreateDirectory(uploads);
                }

                if (!Directory.Exists(tempFolderPath))
                {
                    Directory.CreateDirectory(tempFolderPath);
                }

                if (!Directory.Exists(protectedZipFolderPath))
                {
                    Directory.CreateDirectory(protectedZipFolderPath);
                }

                using (Stream fileStream = new FileStream(zipFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(fileStream);
                }

                _zipUtility.ExtractZipFile(zipFilePath, null, zipFolderPath);
                List<IFileProtectResponse> processDirectoryResp = await ProcessDirectory(zipFolderPath, protectedZipFolderPath);
                string statusMessage = string.Empty;
                processDirectoryResp.Cast<IFileProcessStatus>().ToList().ForEach(x =>
                {
                    if (!string.IsNullOrWhiteSpace(x.ErrorMessage))
                    {
                        statusMessage += $"An error {x.ErrorMessage} occurred while processing the file {x.FileName}{Environment.NewLine}";
                    }
                    else
                    {
                        statusMessage += $"File {x.FileName} is successfully processed.{Environment.NewLine}";
                    }

                    using StreamWriter sw = System.IO.File.CreateText(Path.Combine(protectedZipFolderPath, Constants.STATUS_FILE));
                    sw.WriteLine(statusMessage);
                });

                if (processDirectoryResp.All(x => !string.IsNullOrEmpty(x.ErrorMessage)))
                {
                    return BadRequest("Input file could not be read.");
                }

                _zipUtility.CreateZipFile($"{protectedZipFolderPath}.{FileType.Zip}", null, protectedZipFolderPath);
                AmazonS3Client amazonS3Client = new AmazonS3Client(Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AWS_ACCESS_KEY_ID),
                                                                   Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AWS_SECRET_ACCESS_KEY),
                                                                   RegionEndpoint.EUWest1);
                AmazonS3Uri amazonS3Uri = new AmazonS3Uri(targetPresignedURL);
                using (Stream fs = System.IO.File.OpenRead($"{protectedZipFolderPath}.{FileType.Zip}"))
                {
                    AmazonS3Uri amazonS3TargetUri = new AmazonS3Uri(targetPresignedURL);
                    PutObjectRequest putRequest = new PutObjectRequest()
                    {
                        InputStream = fs,
                        BucketName = amazonS3TargetUri.Bucket,
                        Key = amazonS3TargetUri.Key
                    };

                    PutObjectResponse response = await amazonS3Client.PutObjectAsync(putRequest);
                    if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
                    {
                        return BadRequest("S3 target updation failed.");
                    }

                    return Ok();
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"Exception occured processing file: {e.Message}");
                throw;
            }
            finally
            {
                if (Directory.Exists(tempFolderPath))
                    Directory.Delete(tempFolderPath, true);
            }
        }

        private async Task<List<IFileProtectResponse>> ProcessDirectory(string zipFolderPath,
                                                          string protectedZipFolderPath)
        {
            List<IFileProtectResponse> responseList = new List<IFileProtectResponse>();
            // Process the list of files found in the directory.
            foreach (string extractedFile in Directory.GetFiles(zipFolderPath))
            {
                IFileProtectResponse processFileResp = ICapProcessFile(extractedFile, protectedZipFolderPath);
                responseList.Add(processFileResp);
            }

            // Recurse into subdirectories of this directory.
            foreach (string subdirectory in Directory.GetDirectories(zipFolderPath))
            {
                if (subdirectory.EndsWith(Constants.MACOSX))
                    continue;

                List<IFileProtectResponse> processDirectoryResp = await ProcessDirectory(subdirectory, protectedZipFolderPath);
                responseList.AddRange(processDirectoryResp);
            }
            return responseList;
        }

        private IFileProtectResponse ICapProcessFile(string extractedFile, string protectedZipFolderPath)
        {
            using FileStream stream = System.IO.File.OpenRead(extractedFile);
            IFormFile iFormFile = new FormFile(stream, 0, stream.Length, null, Path.GetFileName(stream.Name));

            string fileName = Path.GetFileName(extractedFile);
            IFileProcessStatus fileProcessStatus = new FileProcessStatus { FileName = fileName };
            if (!TryReadFormFile(iFormFile, out byte[] fileBytes))
            {
                fileProcessStatus.ErrorMessage = "Input file could not be read.";
                fileProcessStatus.StatusCode = 400;
                fileProcessStatus.FileName = fileName;
                return fileProcessStatus;
            }

            ProcessStartInfo processStartInfo = new ProcessStartInfo
            {
                Verb = Constants.OPEN,
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = Constants.BASH
            };

            string directory = Path.GetDirectoryName(extractedFile);
            processStartInfo.WorkingDirectory = directory;
            processStartInfo.UseShellExecute = false;
            processStartInfo.CreateNoWindow = true;
            processStartInfo.RedirectStandardOutput = true;
            processStartInfo.RedirectStandardError = true;
            string outputPath = Path.Combine(protectedZipFolderPath, fileName);

            try
            {
                processStartInfo.Arguments = string.Format(Constants.GwIcap.ARGUMENT, directory, fileName, outputPath);
                string cmdOutput = string.Empty;
                string output = string.Empty;
                using (Process process = new Process())
                {
                    process.StartInfo = processStartInfo;
                    process.Start();
                    process.WaitForExit();
                    using StreamReader streamReader = process.StandardError;
                    cmdOutput = streamReader.ReadToEnd();
                }

                bool? isSuccess = cmdOutput?.Split(Constants.RESPMOD_HEADERS, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)?.Contains(Constants.OK);
                fileProcessStatus.FileName = fileName;
                if (isSuccess.GetValueOrDefault())
                {
                    fileProcessStatus.ProtectedFile = System.IO.File.ReadAllBytes(outputPath);
                    fileProcessStatus.StatusCode = 200;
                    fileProcessStatus.Outcome = Core.Engine.Common.EngineOutcome.Success;
                }
                else
                {
                    fileProcessStatus.StatusCode = 422;
                    fileProcessStatus.Outcome = Core.Engine.Common.EngineOutcome.Error;
                    fileProcessStatus.ErrorMessage = cmdOutput;
                }

                return fileProcessStatus;
            }
            catch (Exception e)
            {
                fileProcessStatus.ErrorMessage = e.Message;
                fileProcessStatus.Outcome = Core.Engine.Common.EngineOutcome.Error;
                fileProcessStatus.IsDisallowed = false;
                return fileProcessStatus;
            }
        }
    }
}
