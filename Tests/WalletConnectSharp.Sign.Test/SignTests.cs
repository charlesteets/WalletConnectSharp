using WalletConnectSharp.Common.Model.Errors;
using WalletConnectSharp.Common.Utils;
using WalletConnectSharp.Network.Models;
using WalletConnectSharp.Sign.Interfaces;
using WalletConnectSharp.Sign.Models;
using WalletConnectSharp.Sign.Models.Engine;
using WalletConnectSharp.Tests.Common;
using Xunit;

namespace WalletConnectSharp.Sign.Test
{
    public class SignTests : IClassFixture<SignClientFixture>
    {
        private SignClientFixture _cryptoFixture;
        private const string AllowedChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        [RpcMethod("test_method"), RpcRequestOptions(Clock.ONE_MINUTE, 99998)]
        public class TestRequest
        {
            public int a;
            public int b;
        }
        
        [RpcMethod("test_method_2"), 
         RpcRequestOptions(Clock.ONE_MINUTE, 99997), 
         RpcResponseOptions(Clock.ONE_MINUTE, 99996)]
        public class TestRequest2
        {
            public string x;
            public int y;
        }

        [RpcResponseOptions(Clock.ONE_MINUTE, 99999)]
        public class TestResponse
        {
            public int result;
        }

        public WalletConnectSignClient ClientA
        {
            get
            {
                return _cryptoFixture.ClientA;
            }
        }
        
        public WalletConnectSignClient ClientB
        {
            get
            {
                return _cryptoFixture.ClientB;
            }
        }

        public SignTests(SignClientFixture cryptoFixture)
        {
            this._cryptoFixture = cryptoFixture;
        }

        public static async Task<SessionStruct> TestConnectMethod(ISignClient clientA, ISignClient clientB)
        {
            var start = Clock.NowMilliseconds();
            
            var testAddress = "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045";
            var dappConnectOptions = new ConnectOptions()
            {
                RequiredNamespaces = new RequiredNamespaces()
                {
                    {
                        "eip155", new ProposedNamespace()
                        {
                            Methods = new[]
                            {
                                "eth_sendTransaction",
                                "eth_signTransaction",
                                "eth_sign",
                                "personal_sign",
                                "eth_signTypedData",
                            },
                            Chains = new[]
                            {
                                "eip155:1"
                            },
                            Events = new[]
                            {
                                "chainChanged", "accountsChanged"
                            }
                        }
                    }
                }
            };

            var dappClient = clientA;
            var connectData = await dappClient.Connect(dappConnectOptions);

            var walletClient = clientB;
            var proposal = await walletClient.Pair(connectData.Uri);
            
            Assert.NotNull(proposal.RequiredNamespaces);
            Assert.NotNull(proposal.OptionalNamespaces);
            Assert.True(proposal.SessionProperties == null || proposal.SessionProperties.Count > 0);
            Assert.NotNull(proposal.Expiry);
            Assert.NotNull(proposal.Id);
            Assert.NotNull(proposal.Relays);
            Assert.NotNull(proposal.Proposer);
            Assert.NotNull(proposal.PairingTopic);

            var approveData = await walletClient.Approve(proposal, testAddress);

            var sessionData = await connectData.Approval;
            await approveData.Acknowledged();

            return sessionData;
        }

        [Fact, Trait("Category", "integration")]
        public async void TestApproveSession()
        {
            await _cryptoFixture.WaitForClientsReady();

            await TestConnectMethod(ClientA, ClientB);
        }
        
        [Fact, Trait("Category", "integration")]
        public async void TestRejectSession()
        {
            await _cryptoFixture.WaitForClientsReady();
            
            var testAddress = "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045";
            var dappConnectOptions = new ConnectOptions()
            {
                RequiredNamespaces = new RequiredNamespaces()
                {
                    {
                        "eip155", new ProposedNamespace()
                        {
                            Methods = new[]
                            {
                                "eth_sendTransaction",
                                "eth_signTransaction",
                                "eth_sign",
                                "personal_sign",
                                "eth_signTypedData",
                            },
                            Chains = new[]
                            {
                                "eip155:1"
                            },
                            Events = new[]
                            {
                                "chainChanged", "accountsChanged"
                            }
                        }
                    }
                }
            };

            var dappClient = ClientA;
            var connectData = await dappClient.Connect(dappConnectOptions);

            var walletClient = ClientB;
            var proposal = await walletClient.Pair(connectData.Uri);

            await walletClient.Reject(proposal);

            await Assert.ThrowsAsync<WalletConnectException>(() => connectData.Approval);
        }
        
        [Fact, Trait("Category", "integration")]
        public async void TestSessionRequestResponse()
        {
            await _cryptoFixture.WaitForClientsReady();
            
            var testAddress = "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045";
            var testMethod = "test_method";
            
            var dappConnectOptions = new ConnectOptions()
            {
                RequiredNamespaces = new RequiredNamespaces()
                {
                    {
                        "eip155", new ProposedNamespace()
                        {
                            Methods = new[]
                            {
                                testMethod
                            },
                            Chains = new[]
                            {
                                "eip155:1"
                            },
                            Events = new[]
                            {
                                "chainChanged", "accountsChanged"
                            }
                        }
                    }
                }
            };

            var dappClient = ClientA;
            var connectData = await dappClient.Connect(dappConnectOptions);

            var walletClient = ClientB;
            var proposal = await walletClient.Pair(connectData.Uri);

            var approveData = await walletClient.Approve(proposal, testAddress);

            var sessionData = await connectData.Approval;
            await approveData.Acknowledged();

            var rnd = new Random();
            var a = rnd.Next(100);
            var b = rnd.Next(100);

            var testData = new TestRequest()
            {
                a = a,
                b = b,
            };

            var pending = new TaskCompletionSource<int>();
            
            // Step 1. Setup event listener for request
            
            // The wallet client will listen for the request with the "test_method" rpc method
            walletClient.Engine.SessionRequestEvents<TestRequest, TestResponse>()
                    .OnRequest += ( requestData) =>
                {
                    var request = requestData.Request;
                    var data = request.Params;

                    requestData.Response = new TestResponse()
                    {
                        result = data.a * data.b
                    };
                    
                    return Task.CompletedTask;
                };

            // The dapp client will listen for the response
            // Normally, we wouldn't do this and just rely on the return value
            // from the dappClient.Engine.Request function call (the response Result or throws an Exception)
            // We do it here for the sake of testing
            dappClient.Engine.SessionRequestEvents<TestRequest, TestResponse>()
                .FilterResponses((r) => r.Topic == sessionData.Topic)
                .OnResponse += (responseData) =>
            {
                var response = responseData.Response;
                
                var data = response.Result;

                pending.SetResult(data.result);

                return Task.CompletedTask;
            };
            
            // 2. Send the request from the dapp client
            var responseReturned = await dappClient.Engine.Request<TestRequest, TestResponse>(sessionData.Topic, testData);
            
            // 3. Wait for the response from the event listener
            var eventResult = await pending.Task.WithTimeout(TimeSpan.FromSeconds(5));
            
            Assert.Equal(eventResult, a * b);
            Assert.Equal(eventResult, testData.a * testData.b);
            Assert.Equal(eventResult, responseReturned.result);
        }
        
        [Fact, Trait("Category", "integration")]
        public async void TestTwoUniqueSessionRequestResponse()
        {
            await _cryptoFixture.WaitForClientsReady();
            
            var testAddress = "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045";
            var testMethod = "test_method";
            var testMethod2 = "test_method_2";

            var dappConnectOptions = new ConnectOptions()
            {
                RequiredNamespaces = new RequiredNamespaces()
                {
                    {
                        "eip155", new ProposedNamespace()
                        {
                            Methods = new[]
                            {
                                testMethod,
                                testMethod2
                            },
                            Chains = new[]
                            {
                                "eip155:1"
                            },
                            Events = new[]
                            {
                                "chainChanged", "accountsChanged"
                            }
                        }
                    }
                }
            };

            var dappClient = ClientA;
            var connectData = await dappClient.Connect(dappConnectOptions);

            var walletClient = ClientB;
            var proposal = await walletClient.Pair(connectData.Uri);

            var approveData = await walletClient.Approve(proposal, testAddress);

            var sessionData = await connectData.Approval;
            await approveData.Acknowledged();

            var rnd = new Random();
            var a = rnd.Next(100);
            var b = rnd.Next(100);
            var x = rnd.NextStrings(AllowedChars, (Math.Min(a, b), Math.Max(a, b)), 1).First();
            var y = x.Length;

            var testData = new TestRequest() { a = a, b = b, };
            var testData2 = new TestRequest2() { x = x, y = y };

            var pending = new TaskCompletionSource<int>();
            var pending2 = new TaskCompletionSource<bool>();
            
            // Step 1. Setup event listener for request
            
            // The wallet client will listen for the request with the "test_method" rpc method
            walletClient.Engine.SessionRequestEvents<TestRequest, TestResponse>()
                    .OnRequest += ( requestData) =>
                {
                    var request = requestData.Request;
                    var data = request.Params;

                    requestData.Response = new TestResponse()
                    {
                        result = data.a * data.b
                    };
                    
                    return Task.CompletedTask;
                };
            
            // The wallet client will listen for the request with the "test_method" rpc method
            walletClient.Engine.SessionRequestEvents<TestRequest2, bool>()
                .OnRequest += ( requestData) =>
            {
                var request = requestData.Request;
                var data = request.Params;

                requestData.Response = data.x.Length == data.y;
                    
                return Task.CompletedTask;
            };

            // The dapp client will listen for the response
            // Normally, we wouldn't do this and just rely on the return value
            // from the dappClient.Engine.Request function call (the response Result or throws an Exception)
            // We do it here for the sake of testing
            dappClient.Engine.SessionRequestEvents<TestRequest, TestResponse>()
                .FilterResponses((r) => r.Topic == sessionData.Topic)
                .OnResponse += (responseData) =>
            {
                var response = responseData.Response;
                
                var data = response.Result;

                pending.TrySetResult(data.result);

                return Task.CompletedTask;
            };
            
            // 2. Send the request from the dapp client
            var responseReturned = await dappClient.Engine.Request<TestRequest, TestResponse>(sessionData.Topic, testData);
            var responseReturned2 = await dappClient.Engine.Request<TestRequest2, bool>(sessionData.Topic, testData2);
            
            // 3. Wait for the response from the event listener
            var eventResult = await pending.Task.WithTimeout(TimeSpan.FromSeconds(5));
            
            Assert.Equal(eventResult, a * b);
            Assert.Equal(eventResult, testData.a * testData.b);
            Assert.Equal(eventResult, responseReturned.result);
            
            Assert.True(responseReturned2);
        }
    }
}
