using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using Azure;
using Azure.AI.OpenAI;
using System;


using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using System.Collections.Generic;

namespace EchoBot.services
{


    public class ChatGPTClient
    {

        private static string base_setup_msg = "You are an AI Assistant tasked with answering queries from users of "+ AppParameters.BANK_NAME +
            ". You are given the following extracted parts of a long document and a question as input context. Provide a conversational answer, and derive your response from the extracted parts only."+
            " Some rules you must follow: \n - Commence every new conversation with a greeting , saying 'Hello Good Morning/Afternoon! I am your AI Assistant from "+ AppParameters.BANK_NAME +". How can I help you?\n  \n"+
            "- If you don't know the answer to a question, just say 'Hmm, I'm not sure. Do not make up an answer' \n"+
            "- if the entity in the user message is not present in the context or is different from it, respond with 'sorry! I do not have any information to share on this question!'\n";
        private static string base_prompt_msg_prefix = "Given the following conversation and a follow up question, rephrase the follow up question to be a standalone question. \n Chat History:\n";
       
        private static string base_prompt_msg_prefix2 = "\nFollow Up Input: ";
        private static string base_prompt_msg_suffix = "\nStandalone question: ";

        private static string prompt_additional_details_prompt = "\n keep in mind the following additional details: \n";

        string endpoint = AppParameters.OPEN_AI_ENDPOINT;
        OpenAIClient client;


        public ChatGPTClient()
        {
            client = new OpenAIClient(new System.Uri(endpoint), new AzureKeyCredential(AppParameters.OPEN_AI_API_KEY));
        }

        public async Task<string> InitConversationPromptState(ChatCompletionsOptions chatCompletionsOptions, string message)
        {
            string answer = String.Empty;
            chatCompletionsOptions.Messages.Add(new ChatMessage(ChatRole.System, base_setup_msg));
            chatCompletionsOptions.Messages.Add(new ChatMessage(ChatRole.User, message));
            answer = await MakeConversation(chatCompletionsOptions);
            chatCompletionsOptions.Messages.Add(new ChatMessage(ChatRole.Assistant, answer));
            return answer;
        }


        public async Task<string> GetUpdatedConversationState( ChatCompletionsOptions chatCompletionsOptions, string query)
        {
            string answer = String.Empty;
            System.Console.WriteLine("***************  running new user query from the Bot ****************");
            System.Console.WriteLine("user query from the Bot :" + query);

            // Get the standalone question, based on the converation context and the follow up question
            string standalone_question =  GetStandaloneQuestion(chatCompletionsOptions, query);
            string search_content = loadConversationContext(standalone_question);
            System.Console.WriteLine("search_content retrieved for the standalone question:\n" + search_content);
            chatCompletionsOptions.Messages[0] = new ChatMessage(ChatRole.System, base_setup_msg +search_content);
            // Update the conversation context with the new user query
            chatCompletionsOptions.Messages.Add(new ChatMessage(ChatRole.User, standalone_question + prompt_additional_details_prompt + query));
            answer = await MakeConversation(chatCompletionsOptions);
            chatCompletionsOptions.Messages.Add(new ChatMessage(ChatRole.Assistant, answer));
            return answer;
        }


        public async Task<string> MakeConversation(ChatCompletionsOptions chatCompletionsOptions)
        {
            string response_msg = string.Empty;


            //chatCompletionsOptions.Messages = currentConversationState;

            Response<StreamingChatCompletions> response = null;
            try
            {
                response = await client.GetChatCompletionsStreamingAsync(deploymentOrModelName: AppParameters.OPEN_AI_MODEL_DEPLOYMENT_NAME, chatCompletionsOptions);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error running ChatGPT Bot " + ex.StackTrace);
                response_msg = ex.Message;
                return response_msg;
            }
            using StreamingChatCompletions streamingChatCompletions = response.Value;

            await foreach (StreamingChatChoice choice in streamingChatCompletions.GetChoicesStreaming())
            {

                await foreach (ChatMessage message in choice.GetMessageStreaming())
                {

                    response_msg += message.Content;
                }
            }
            Console.WriteLine("the answer to the question from ChatGPT\n" + response_msg);
            return response_msg;

        }


        private string loadConversationContext(string query_intent)
        {
            // Get the service endpoint and API key from the environment
            Uri endpoint = new Uri(AppParameters.SEMANTIC_SEARCH_ENDPOINT);
            AzureKeyCredential credential = new AzureKeyCredential(AppParameters.SEMANTIC_SEARCH_API_KEY);

            // Create a new SearchIndexClient
            SearchIndexClient indexClient = new SearchIndexClient(endpoint, credential);

            // Perform an operation
            Response<SearchServiceStatistics> stats = indexClient.GetServiceStatistics();
            // Console.WriteLine($"You are using {stats.Value.Counters.IndexCounter.Usage} indexes.");

            SearchOptions options = new SearchOptions();


            // This configuration is for school course content where data in data lake gen2
            SearchClient s_client = indexClient.GetSearchClient(AppParameters.CURRICULUM_INDEX_NAME);
            options.SemanticConfigurationName = AppParameters.CURRICULUM_SEMANTIC_CONFIG;

            options.QueryType = SearchQueryType.Semantic;
            options.QueryLanguage = "en-us";
            options.QuerySpeller = QuerySpellerType.Lexicon;
            options.QueryCaption = QueryCaptionType.Extractive;
            options.QueryAnswer = QueryAnswerType.Extractive;
            options.QueryAnswerCount = 3;


            var results = s_client.Search<SearchDocument>(query_intent, options).Value.GetResults();

            // get answer from search result and not entire content
            string inputPrompt = "------  Input context -------\n";

            int numRows = 0;
            foreach (SearchResult<SearchDocument> result in results)
            {
                if (numRows == 2)
                    break;
                // Console.WriteLine(result.Captions[0].Text);
                if (result.Captions is null)
                {
                    Console.WriteLine("Semantic search did not return any results");
                    continue;
                }
                else
                {
                    try
                    {
                        string file_name = result.Document["metadata_storage_name"].ToString();
                        inputPrompt += "Document Name: " + file_name + "\n";
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error in getting the caption from the semantic search result" + ex.StackTrace);
                    }

                    inputPrompt += result.Document["content"];
                    inputPrompt += "\n--------------------------------------------------\n";

                }
                numRows++;
            }
            return inputPrompt;
        }


        private string GetStandaloneQuestion(ChatCompletionsOptions chatCompletionsOptions, string inputQuery)
        {
            string core_query = string.Empty;
            string request_prompt = base_prompt_msg_prefix;

            foreach (ChatMessage message in chatCompletionsOptions.Messages)
            {
                request_prompt += message.Content;
            }
            request_prompt += base_prompt_msg_prefix2;
            request_prompt += inputQuery;
            request_prompt += base_prompt_msg_suffix;

            Console.WriteLine("The Intent extraction query to Open Api is>>>>>>>>>>>>>>>>:\n" + request_prompt);
            CompletionsOptions completionsOptions = new CompletionsOptions();
            completionsOptions.MaxTokens = 1000;

            completionsOptions.Prompts.Add(request_prompt);

            Response<Completions> completionsResponse = client.GetCompletions(AppParameters.OPEN_AI_MODEL_DEPLOYMENT_NAME_COMPLETIONS_END_POINT, completionsOptions);
            foreach (Choice responsedata in completionsResponse.Value.Choices)
            {
                core_query = responsedata.Text;
                Console.WriteLine(responsedata.Text);
                Console.WriteLine("------  end of intent extraction ----------");
            }
            Console.WriteLine("The standalone question from Open Api is:\n" + core_query);
            return core_query;
        }

    }


}