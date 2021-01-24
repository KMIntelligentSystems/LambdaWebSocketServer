using System;
using Amazon.Lambda;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using Newtonsoft.Json;


using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.Runtime;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.ApiGatewayManagementApi;
using Amazon.SQS;
using Amazon.SQS.Model;
using Newtonsoft.Json.Linq;
using System.Linq;
using Amazon.Lambda.SNSEvents;
using Amazon.SimpleNotificationService;
using Amazon.Runtime.SharedInterfaces;

//The WebSocket triggers a Lambda function which creates a record in Dynamo DB. 
//The record is a key-value mapping of Execution ARN – ConnectionId.
//[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]
namespace AWSServerless2
{
    /// <summary>
    /// This class extends from APIGatewayProxyFunction which contains the method FunctionHandlerAsync which is the 
    /// actual Lambda function entry point. The Lambda handler field should be set to
    /// 
    /// MyHttpGatewayApi::MyHttpGatewayApi.LambdaEntryPoint::FunctionHandlerAsync
    /// </summary>
    public class LambdaEntryPoint :

        // The base class must be set to match the AWS service invoking the Lambda function. If not Amazon.Lambda.AspNetCoreServer
        // will fail to convert the incoming request correctly into a valid ASP.NET Core request.
        //
        // API Gateway REST API                         -> Amazon.Lambda.AspNetCoreServer.APIGatewayProxyFunction
        // API Gateway HTTP API payload version 1.0     -> Amazon.Lambda.AspNetCoreServer.APIGatewayProxyFunction
        // API Gateway HTTP API payload version 2.0     -> Amazon.Lambda.AspNetCoreServer.APIGatewayHttpApiV2ProxyFunction
        // Application Load Balancer                    -> Amazon.Lambda.AspNetCoreServer.ApplicationLoadBalancerFunction
        // 
        // Note: When using the AWS::Serverless::Function resource with an event type of "HttpApi" then payload version 2.0
        // will be the default and you must make Amazon.Lambda.AspNetCoreServer.APIGatewayHttpApiV2ProxyFunction the base class.

        Amazon.Lambda.AspNetCoreServer.APIGatewayHttpApiV2ProxyFunction
    {
        string ConnectionMappingTable { get; set; }

        /// <summary>
        /// DynamoDB service client used to store and retieve connection information from the ConnectionMappingTable
        /// </summary>
        IAmazonDynamoDB DDBClient { get; set; }
        IAmazonSQS SQSClient { get; set; }
        public const string ConnectionIdField = "ConnectionIdField";
        
        
          Func<string, IAmazonApiGatewayManagementApi> ApiGatewayManagementApiClientFactory { get; set; }
        /// <summary>
        /// The builder has configuration, logging and Amazon API Gateway already configured. The startup class
        /// needs to be configured in this method using the UseStartup<>() method.
        /// </summary>
        /// <param name="builder"></param>
        protected override void Init(IWebHostBuilder builder)
        {
            DDBClient = new AmazonDynamoDBClient();
            SQSClient = new AmazonSQSClient();

            this.ApiGatewayManagementApiClientFactory = (Func<string, AmazonApiGatewayManagementApiClient>)((endpoint) =>
            {
                return new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig
                {
                    ServiceURL = endpoint
                });
            });
            builder
                .UseStartup<Startup>();
        }

        /// <summary>
        /// Use this override to customize the services registered with the IHostBuilder. 
        /// 
        /// It is recommended not to call ConfigureWebHostDefaults to configure the IWebHostBuilder inside this method.
        /// Instead customize the IWebHostBuilder in the Init(IWebHostBuilder) overload.
        /// </summary>
        /// <param name="builder"></param>
        protected override void Init(IHostBuilder builder)
        {
            DDBClient = new AmazonDynamoDBClient();
            this.ApiGatewayManagementApiClientFactory = (Func<string, AmazonApiGatewayManagementApiClient>)((endpoint) =>
            {
                return new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig
                {
                    ServiceURL = endpoint
                });
            });
        }

        public async Task<APIGatewayProxyResponse> OnConnectHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {

            
            try
            {
                ConnectionMappingTable = "LambdaDb";
                var connectionId = request.RequestContext.ConnectionId;
                var domainName = request.RequestContext.DomainName;
                var stage = request.RequestContext.Stage;
                var endpoint = $"https://{domainName}/{stage}";
               context.Logger.LogLine($"ConnectionId: {connectionId}");
                Console.WriteLine("Req = " + request.RequestContext);

                var ddbRequest = new PutItemRequest
                {
                    TableName = ConnectionMappingTable,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        {ConnectionIdField, new AttributeValue{ S = connectionId}},
                        {"Endpoint", new AttributeValue{ S = endpoint}}

                    }
                };

                await DDBClient.PutItemAsync(ddbRequest);

             
                var receivedWebSocketKey = request.Headers["Sec-WebSocket-Key"];
                const string eol = "\r\n";

                var keyHash = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(receivedWebSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));

                var response = "HTTP/1.1 101 Switching Protocols" + eol;
                response += "Connection: Upgrade" + eol;
                response += "Upgrade: websocket" + eol;
                response += "Sec-WebSocket-Accept: " + Convert.ToBase64String(keyHash) + eol;
                response += "Sec-WebSocket-Protocol: graphql-ws" + eol;
                response += eol;
                Console.WriteLine("Response = " + response);
                IDictionary<string, string> headers = new Dictionary<string, string>();
                headers.Add("Sec-WebSocket-Accept", receivedWebSocketKey);
                headers.Add("Sec-WebSocket-Protocol", "graphql-ws");

                
                //Amazon.S3.Model.PutObjectRequest s3Request = new Amazon.S3.Model.PutObjectRequest() { BucketName = "kim.mcclymont.myOtherBucket", Key = "myKey", ContentBody = "sample text" };

                //   S3Client = new AmazonS3Client();
                //   await S3Client.PutObjectAsync(s3Request);

                return new APIGatewayProxyResponse
                {
                    StatusCode = 200,
                    Headers = headers
                  //  Body = resp
                };
            }
           
            catch (Exception e)
            {
                Console.WriteLine("Error = " + e.Message);
                context.Logger.LogLine("Error connecting: " + e.Message);
                context.Logger.LogLine(e.StackTrace);
                return new APIGatewayProxyResponse
                {
                    StatusCode = 500,
                    Body = $"Failed to connect: {e.Message}"
                };
            }
            
        }
        bool hasResult = false;
        string result;
        public async Task<APIGatewayProxyResponse> SendMessageHandler(MessageType request, ILambdaContext context)
        {
           

            try
            {
              //  await Task.Delay(100);
                await GetGraphQLResponse();

                DefaultLambdaJsonSerializer s = new DefaultLambdaJsonSerializer();
                
                object obj = new { payload = new { data = new { Message = new { content = "Test Testing", SentAt = DateTime.Now.ToString(), MessageFrom = new { Id = "1", DisplayName = "Test" } } } }, type = "data", id = "1" };
               
                MemoryStream stream = new MemoryStream();
                var buffer = new byte[1024 * 4];
                
                if (request != null && request.result != null && request.result.Contains("payload"))
                {
                    buffer = Encoding.UTF8.GetBytes(request.result);
                    stream.Write(buffer, 0, buffer.Length);

                }
                else
                {
                    
                    s.Serialize(obj, stream);
                   // Console.WriteLine($"OBJ {obj}");
                }
               
                var bytes = new byte[stream.Length];
                stream.Seek(0, SeekOrigin.Begin);
                await stream.ReadAsync(bytes, 0, bytes.Length);
                var resp = Encoding.UTF8.GetString(bytes, 0, bytes.Length);

                var scanRequest = new ScanRequest
                {
                    TableName = "LambdaDb",
                    ProjectionExpression = "ConnectionIdField, Endpoint"
                };
               
                var scanResponse = await DDBClient.ScanAsync(scanRequest);
                var item = scanResponse.Items[scanResponse.Items.Count - 1];
                var endpoint = "";
                var count = 0;
               // foreach (var item in scanResponse.Items)
               // {
                    var postConnectionRequest = new PostToConnectionRequest
                    {
                        ConnectionId = item[ConnectionIdField].S,
                        Data = stream
                    };

                    try
                    {
                        endpoint = item["Endpoint"].S;
                        var apiClient = ApiGatewayManagementApiClientFactory(endpoint);
                        context.Logger.LogLine($"Post to connection {count}: {postConnectionRequest.ConnectionId}");
                        stream.Position = 0;
                        await apiClient.PostToConnectionAsync(postConnectionRequest);
                        count++;
                    }
                    catch (AmazonServiceException e)
                    {
                        // API Gateway returns a status of 410 GONE then the connection is no
                        // longer available. If this happens, delete the identifier
                        // from our DynamoDB table.
                        if (e.StatusCode == HttpStatusCode.Gone)
                        {
                            var ddbDeleteRequest = new DeleteItemRequest
                            {
                                TableName = "LambdaDb",
                                Key = new Dictionary<string, AttributeValue>
                                {
                                    {ConnectionIdField, new AttributeValue {S = postConnectionRequest.ConnectionId}}
                                }
                            };

                            context.Logger.LogLine($"Deleting gone connection: {postConnectionRequest.ConnectionId}");
                            await DDBClient.DeleteItemAsync(ddbDeleteRequest);
                        }
                        else
                        {
                            context.Logger.LogLine($"Error posting message to {postConnectionRequest.ConnectionId}: {e.Message}");
                            context.Logger.LogLine(e.StackTrace);
                        }
                    }
                // }

                if (request != null && request.result != null && request.result.Contains("payload"))
                {
                    var data = request.result.Replace(":", " = ");
                    data = data.Replace(@"""", @""); 
                    s.Serialize(data, stream);
                    return new APIGatewayProxyResponse
                    {
                        StatusCode = (int)HttpStatusCode.OK,
                        Body = resp
                    };
                }
            
                
                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = resp
                };
            }
            catch (Exception e)
            {
                context.Logger.LogLine("Error disconnecting: " + e.Message);
                context.Logger.LogLine(e.StackTrace);
                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Body = $"Failed to send message: {e.Message}"
                };
            }
        }

        private async Task<bool> GetGraphQLResponse()
        {
            
            ICoreAmazonSQS sqs = new AmazonSQSClient();
            var client = new AmazonSimpleNotificationServiceClient();
            var res = await client.SubscribeQueueAsync("arn:aws:sns:us-east-1:280449388741:SimpleSNS", sqs, "https://sqs.us-east-1.amazonaws.com/280449388741/GraphQLDataQueue");
            Console.WriteLine($"Test SNS {res}");
            
            return true;
        }

        public async Task<APIGatewayProxyResponse> OnDisconnectHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var connectionId = request.RequestContext.ConnectionId;
                context.Logger.LogLine($"ConnectionId: {connectionId}");

             
                return new APIGatewayProxyResponse
                {
                    StatusCode = 200,
                    Body = "Disconnected."
                };
            }
            catch (Exception e)
            {
                context.Logger.LogLine("Error disconnecting: " + e.Message);
                context.Logger.LogLine(e.StackTrace);
                return new APIGatewayProxyResponse
                {
                    StatusCode = 500,
                    Body = $"Failed to disconnect: {e.Message}"
                };
            }
        }
        
        public async Task<APIGatewayProxyResponse> Get(APIGatewayProxyRequest request, ILambdaContext context)
        {
            var sqsRequest = new SendMessageRequest
            {
                QueueUrl = "https://sqs.us-east-1.amazonaws.com/280449388741/WorflowQueue", 
                MessageBody = request.Body
            };
        
            await SQSClient.SendMessageAsync(sqsRequest);
            context.Logger.LogLine($"Get Request { request.Body}");
            var obj = new { payload = new { data = new { message = new { content = "Test", sentAt = DateTime.Now.ToString(), messageFrom = new { id = "1", displayName = "Test" } } } }, type = "data", id = "1" };
            var resp = JsonConvert.SerializeObject(obj);

            Console.WriteLine($"RESPONSE TEXT {resp}");
            var response = new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = resp,
                Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } }
            };
            return response;
        }
       // [assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]
        public async Task<MessageType> SendGraphQLResponse(MessageType request, ILambdaContext context)
        {
            await Task.Delay(50000);
            return request;
        }
    }
}
