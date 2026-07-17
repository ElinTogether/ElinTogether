using System.Collections.Generic;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class QuestCreateDelta : ElinDelta
{
    [IgnoreMember]
    public Chara? Owner;

    [IgnoreMember]
    public int Uid;

    [Key(0)]
    public required LZ4Bytes Data { get; set; }

    [Key(1)]
    public required bool IsGlobal { get; set; }

    protected override void OnApply(ElinNetBase net)
    {
        if (NetSession.Instance.IsHost) {
            return;
        }

        var quest = Data.Decompress<Quest>();
        if (quest.person.chara is not { } chara) {
            return;
        }

        quest.SetClient(chara, !IsGlobal);

        if (IsGlobal) {
            game.quests.globalList.Add(quest);
        }
    }

    public static QuestCreateDelta Create(Quest quest)
    {
        return new() {
            Uid = quest.uid,
            Owner = quest.person.chara,
            Data = LZ4Bytes.Create(quest),
            // unknown at the moment
            IsGlobal = false,
        };
    }

    internal static void Refresh(List<ElinDelta> deltaList)
    {
        var alreadySent = new List<int>();
        deltaList.RemoveAll(delta => {
            if (delta is not QuestCreateDelta questCreateDelta) {
                return false;
            }

            var quest = game.quests.globalList.Find(q => q.uid == questCreateDelta.Uid);
            if (quest is not null) {
                questCreateDelta.IsGlobal = true;
            } else {
                quest = questCreateDelta.Owner?.quest;
                if (quest?.uid != questCreateDelta.Uid) {
                    return true;
                }
            }

            alreadySent.Add(quest.uid);
            questCreateDelta.Data = LZ4Bytes.Create(quest);
            return false;
        });

        deltaList.RemoveAll(delta => delta is QuestSetClientDelta questSetClientDelta &&
                                     alreadySent.Contains(questSetClientDelta.Uid));
    }
}