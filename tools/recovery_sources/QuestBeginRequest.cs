extern alias game;

using System;
using System.Collections;
using System.Collections.Generic;
using EB;
using EB.Sparx;

using QuestsApi = game::Legacy.QuestsAPI;

namespace RecoverySources
{
    // Newly authored AI-assisted recovery code based on the retained 9.2
    // ARM64 traces. The compile-time callback type is mapped to the game's
    // generated Legacy.QuestsAPI closure by the Cecil importer.
    public sealed class QuestBeginResponse
    {
        public EB.Action<string, IDictionary> callback;

        public void Handle(Response response)
        {
            EB.Action<string, IDictionary> completion = this.callback;
            if (response.sucessful)
            {
                completion(null, response.hashtable);
                return;
            }

            if (response.error == null)
            {
                completion(response.localizedError, null);
                return;
            }

            string code = response.error.ToString();
            if (code == "nsf")
            {
                Hub hub = Hub.Instance;
                RedeemerManager redeemer = hub.RedeemerManager;
                IDictionary data = response.hashtable;
                var item = new RedeemerItem(data);
                completion(redeemer.GetNSFErrorMessage(item), data);
                return;
            }

            if (code == "costsChanged")
            {
                completion(response.localizedError, null);
            }
            else
            {
                completion(code, null);
            }
        }
    }

    // These methods are compile-time bridges only. The importer maps their
    // call sites to the existing private 9.2 QuestsAPI.Post and to a small
    // in-type service wrapper, so no RecoverySources type survives in game IL.
    public static class QuestBeginEndpointBridge
    {
        public static Request Post(QuestsApi api, string path)
        {
            throw new NotSupportedException();
        }

        public static void Service(QuestsApi api, Request request,
            EB.Action<Response> callback)
        {
            throw new NotSupportedException();
        }
    }

    public static class QuestBeginRequest
    {
        public static void Begin(QuestsApi api, string qid, string hash,
            string setId, List<string> team, int difficulty,
            List<RedeemerItem> expectedCost, bool quitExisting,
            EB.Action<string, IDictionary> callback)
        {
            var response = new QuestBeginResponse { callback = callback };
            Request request = QuestBeginEndpointBridge.Post(api,
                string.Concat("/quests/quest-begin/", qid));

            request.AddData("hash", hash);
            request.AddData("setId", setId);
            request.AddData("quitExisting", quitExisting);
            if (difficulty != 0)
            {
                request.AddData("difficulty", difficulty);
            }

            if (expectedCost != null)
            {
                var costs = new ArrayList();
                for (int i = 0; i < expectedCost.Count; i++)
                {
                    costs.Add(expectedCost[i].toHashTable());
                }
                request.AddData("costs", costs);
            }

            if (team != null)
            {
                for (int i = 0; i < team.Count; i++)
                {
                    request.AddData("tm" + i.ToString(), team[i]);
                }
            }

            QuestBeginEndpointBridge.Service(api, request,
                new EB.Action<Response>(response.Handle));
        }
    }
}
