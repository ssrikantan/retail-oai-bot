using System.Collections.Generic;
using Azure.AI.OpenAI;

namespace EchoBot.State
{

    public class ConversationData
    {
        // The time-stamp of the most recent incoming message.
        public string Timestamp { get; set; }

        // The ID of the user's channel.
        public string ChannelId { get; set; }

        // Track whether we have already asked the user's name
        public bool PromptedUserForName { get; set; } = false;


        public ChatCompletionsOptions chatCompletionsOptions {get; set;}


    }
}