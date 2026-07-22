using System.Collections.Generic;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class QuestCreateDelta : ElinDelta
{
    private static readonly HashSet<int> _createdInCurrentFrame = [];

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
        _createdInCurrentFrame.Add(quest.uid);
        return new() {
            Uid = quest.uid,
            Owner = quest.person.chara,
            Data = null!,
            // unknown at the moment
            IsGlobal = false,
        };
    }

    internal override bool OnRefresh()
    {
        var quest = game.quests.globalList.Find(q => q.uid == Uid);
        if (quest is not null) {
            IsGlobal = true;
        } else {
            quest = Owner?.quest;
            if (quest?.uid != Uid) {
                return false;
            }
        }

        Data = LZ4Bytes.Create(quest);
        return true;
    }

    internal static bool Contains(int uid)
    {
        return _createdInCurrentFrame.Contains(uid);
    }

    internal static void ClearRecordedUids()
    {
        _createdInCurrentFrame.Clear();
    }
}