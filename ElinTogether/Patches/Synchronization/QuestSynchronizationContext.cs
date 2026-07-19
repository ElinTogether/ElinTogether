namespace ElinTogether.Patches;

internal class QuestSynchronizationContext : SynchronizationContext
{
    // private static Dictionary<int, string> Store = [];

    internal static void Update()
    {
        game.quests.list.RemoveAll(q => q.uid < 0);
        game.quests.globalList.RemoveAll(q => q.uid < 0);

        // static void CheckQuest(Quest q)
        // {
        //     var str = q.ToCompactJson();
        //     var bytes = Encoding.UTF8.GetBytes(str);
        //     var hashBytes = System.Security.Cryptography.SHA256.Create().ComputeHash(bytes);
        //     var newHash = BitConverter.ToString(hashBytes).Replace("-", "");

        //     if (Store.TryGetValue(q.uid, out var hash) && hash != newHash) {
        //         NetSession.Instance.Connection!.Delta.AddRemote(QuestUpdateDelta.Create(q));
        //     }

        //     Store[q.uid] = newHash;
        // }
    }
}