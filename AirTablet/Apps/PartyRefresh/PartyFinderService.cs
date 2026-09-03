using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PartyRefresh;

internal sealed class PartyFinderService
{
    private const string OpenActiveRecruitmentSignature = "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 07 C6 83 ?? ?? ?? ?? ?? 48 83 C4 20 5B C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 40 53";
    private const int CommentOffset = 0x330;
    private const int LanguageCheckboxesOffset = 0x378;
    private const int SpecificDutyFlagOffset = 0x12;
    private const int SlotFlagsOffset = 0x1B0;
    private static readonly uint[] CombatJobIds =
    [
        1, 2, 3, 4, 5, 6, 7,
        19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43,
    ];

    private readonly Configuration config;
    private readonly Func<PartyFinderPreset> activePreset;
    private readonly OpenActiveRecruitmentDelegate? openActiveRecruitment;
    private OperationStep step;
    private PartyFinderPreset? pendingPreset;
    private bool refreshOnly;
    private bool endingRecruitment;
    private int dutyDropdownAttempts;
    private int confirmationChecks;
    private long nextStepAt;
    private long operationDeadline;
    private readonly RefreshSchedule refreshSchedule = new();
    private bool criteriaSubmitted;
    private bool criteriaWasVisible;
    private string? notification;

    public PartyFinderService(Configuration config, Func<PartyFinderPreset> activePreset)
    {
        this.config = config;
        this.activePreset = activePreset;
        try
        {
            var address = DalamudServices.SigScanner.ScanText(OpenActiveRecruitmentSignature);
            openActiveRecruitment = Marshal.GetDelegateForFunctionPointer<OpenActiveRecruitmentDelegate>(address);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "PartyRefresh could not resolve the active recruitment opener.");
        }
        RefreshScheduleChanged();
    }

    public bool IsBusy => step != OperationStep.Idle;
    public bool IsRecruiting => DalamudServices.ClientState.IsLoggedIn &&
        DalamudServices.ObjectTable.LocalPlayer?.OnlineStatus.RowId == 26;
    public string Status { get; private set; } = "Ready.";
    public DateTime NextAutomaticRefreshUtc => DateTime.UtcNow + AutomaticRefreshRemaining;
    public TimeSpan AutomaticRefreshRemaining => config.AutoRefreshEnabled
        ? TimeSpan.FromMilliseconds(refreshSchedule.RemainingMilliseconds(Environment.TickCount64))
        : TimeSpan.Zero;

    public unsafe bool ApplyPreset(PartyFinderPreset preset)
    {
        if (!CanStart(out var error))
        {
            RejectStart(error);
            return false;
        }
        if (IsRecruiting)
        {
            Fail("End the active Party Finder recruitment before posting a new preset. Use Refresh Now to apply the selected preset to the active listing.");
            return false;
        }
        try
        {
            // Seed the backing agent before opening the window so its initial tab and
            // recruitment type are built from the selected preset.
            WritePreset(preset, null);
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "PartyRefresh could not prepare the Party Finder preset.");
            Fail($"The Party Finder preset could not be prepared: {ex.Message}");
            return false;
        }
        pendingPreset = preset;
        criteriaSubmitted = false;
        refreshOnly = false;
        var openCriteria = GetVisibleConditionAddon();
        if (openCriteria is not null)
        {
            openCriteria->AtkUnitBase.Close(true);
            Status = "Closing the current Recruitment Criteria window.";
            step = OperationStep.WaitingToOpenPartyFinder;
            operationDeadline = Environment.TickCount64 + 20_000;
            nextStepAt = Environment.TickCount64 + 300;
            return true;
        }
        Begin("Opening Party Finder to apply the selected preset.");
        return true;
    }

    public bool RefreshCurrent()
    {
        if (!CanStart(out var error))
        {
            RejectStart(error);
            return false;
        }
        if (!IsRecruiting)
        {
            Fail("There is no active Party Finder recruitment to refresh.");
            return false;
        }
        if (openActiveRecruitment is null)
        {
            Fail("The game's active Party Finder recruitment action is unavailable after this game update.");
            return false;
        }
        pendingPreset = activePreset();
        criteriaSubmitted = false;
        refreshOnly = true;
        BeginRefresh();
        return true;
    }

    public unsafe bool EndRecruitment()
    {
        if (!CanStart(out var error))
        {
            RejectStart(error);
            return false;
        }
        if (!IsRecruiting)
        {
            Fail("There is no active Party Finder recruitment to end.");
            return false;
        }
        if (openActiveRecruitment is null)
        {
            Fail("The game's active Party Finder recruitment action is unavailable after this game update.");
            return false;
        }

        pendingPreset = null;
        refreshOnly = false;
        endingRecruitment = true;
        Status = "Opening the active Party Finder recruitment.";
        step = OperationStep.WaitingToEndRecruitment;
        operationDeadline = Environment.TickCount64 + 20_000;
        nextStepAt = Environment.TickCount64 + 100;
        if (GetVisibleAddon("LookingForGroupDetail") is null)
            TryOpenActiveRecruitmentDetails();
        return true;
    }

    public void SetAutoRefresh(bool enabled)
    {
        config.AutoRefreshEnabled = enabled;
        RefreshScheduleChanged();
        DalamudServices.PluginInterface.SavePluginConfig(config);
        Status = enabled
            ? $"Automatic refresh enabled. The next refresh is in {config.RefreshIntervalMinutes} minutes."
            : "Automatic refresh stopped.";
    }

    public void RefreshScheduleChanged()
    {
        refreshSchedule.Reset(Environment.TickCount64, config.RefreshIntervalMinutes);
    }

    public unsafe void Tick()
    {
        var criteriaVisible = GetVisibleConditionAddon() is not null;
        // A manual in-game post/refresh does not necessarily toggle the online
        // recruiting status. Closing its editor starts a fresh full interval too.
        if (!IsBusy && criteriaWasVisible && !criteriaVisible && IsRecruiting) RefreshScheduleChanged();
        criteriaWasVisible = criteriaVisible;
        var refreshDue = refreshSchedule.IsDue(Environment.TickCount64, config.RefreshIntervalMinutes, IsRecruiting, IsBusy || criteriaVisible);
        if (step == OperationStep.Idle)
        {
            if (config.AutoRefreshEnabled && refreshDue) RefreshCurrent();
            return;
        }

        if (Environment.TickCount64 > operationDeadline)
        {
            try
            {
                RecoverOperation();
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(ex, "PartyRefresh could not recover Party Finder automation.");
                Fail($"Party Finder automation stopped: {ex.Message}");
            }
            return;
        }
        if (Environment.TickCount64 < nextStepAt)
            return;

        try
        {
            switch (step)
            {
                case OperationStep.WaitingToOpenPartyFinder:
                    OpenAfterCriteriaCloses();
                    break;
                case OperationStep.WaitingForPartyFinder:
                    AdvanceFromPartyFinder();
                    break;
                case OperationStep.WaitingForDetails:
                    AdvanceFromDetails();
                    break;
                case OperationStep.ConfiguringCriteria:
                    ConfigureCriteria();
                    break;
                case OperationStep.PopulatingDutyDropdown:
                    PopulateDutyDropdown();
                    break;
                case OperationStep.SelectingDuty:
                    SelectDuty();
                    break;
                case OperationStep.SubmittingCriteria:
                    SubmitCriteria();
                    break;
                case OperationStep.Confirming:
                    ConfirmOrFinish();
                    break;
                case OperationStep.WaitingToEndRecruitment:
                    AdvanceEndRecruitment();
                    break;
                case OperationStep.ConfirmingEndRecruitment:
                    ConfirmEndRecruitment();
                    break;
            }
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "PartyRefresh could not advance Party Finder automation.");
            Fail($"Party Finder automation stopped: {ex.Message}");
        }
    }

    public string? ConsumeNotification()
    {
        var value = notification;
        notification = null;
        return value;
    }

    private bool CanStart(out string error)
    {
        if (IsBusy)
        {
            error = "PartyRefresh is already working on Party Finder.";
            return false;
        }
        if (!DalamudServices.ClientState.IsLoggedIn || DalamudServices.ObjectTable.LocalPlayer is null)
        {
            error = "Log into a character before using PartyRefresh.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private unsafe void Begin(string status)
    {
        Status = status;
        step = OperationStep.WaitingForPartyFinder;
        operationDeadline = Environment.TickCount64 + 20_000;
        if (GetVisibleConditionAddon() is not null ||
            GetVisibleAddon("LookingForGroupDetail") is not null ||
            GetVisibleAddon("LookingForGroup") is not null)
        {
            nextStepAt = Environment.TickCount64 + 100;
            return;
        }
        nextStepAt = Environment.TickCount64 + 450;
        ExecuteCommand("/partyfinder");
    }

    private unsafe void BeginRefresh()
    {
        Status = "Opening the active Party Finder recruitment.";
        operationDeadline = Environment.TickCount64 + 20_000;
        nextStepAt = Environment.TickCount64 + 100;

        if (GetVisibleConditionAddon() is not null)
        {
            step = OperationStep.ConfiguringCriteria;
            return;
        }

        step = OperationStep.WaitingForDetails;
        if (GetVisibleAddon("LookingForGroupDetail") is null)
            TryOpenActiveRecruitmentDetails();
    }

    private unsafe void OpenAfterCriteriaCloses()
    {
        if (GetVisibleConditionAddon() is not null)
            return;
        if (pendingPreset is not null)
            WritePreset(pendingPreset, null);
        Begin("Opening Party Finder to apply the selected preset.");
    }

    private unsafe void AdvanceFromPartyFinder()
    {
        if (GetVisibleConditionAddon() is not null)
        {
            step = OperationStep.ConfiguringCriteria;
            nextStepAt = Environment.TickCount64 + 150;
            return;
        }

        var details = GetVisibleAddon("LookingForGroupDetail");
        if (details is not null)
        {
            if (!refreshOnly)
            {
                details->Close(true);
                Status = "Closing conflicting Party Finder details before posting the preset.";
                nextStepAt = Environment.TickCount64 + 300;
                return;
            }
            if (CloseForeignDetails(details)) return;
            if (TryClickPrimaryDetailButton(details) || TryClickButton(details, 109))
            {
                Status = "Opening the editable recruitment criteria.";
                step = OperationStep.WaitingForDetails;
                nextStepAt = Environment.TickCount64 + 400;
            }
            return;
        }

        var main = GetVisibleAddon("LookingForGroup");
        if (main is null)
        {
            ExecuteCommand("/partyfinder");
            nextStepAt = Environment.TickCount64 + 450;
            return;
        }
        if (refreshOnly)
        {
            TryOpenActiveRecruitmentDetails();
            Status = "Opening the active recruitment details.";
            step = OperationStep.WaitingForDetails;
            nextStepAt = Environment.TickCount64 + 400;
            return;
        }
        var mainAddon = (AddonLookingForGroup*)main;
        var recruitButton = mainAddon->RecruitMembersButton;
        if (recruitButton is null)
            recruitButton = main->GetComponentButtonById(46);
        if (recruitButton is not null && recruitButton->IsEnabled && ClickSyntheticAddonButton(main, recruitButton))
        {
            Status = refreshOnly
                ? "Opening the active recruitment details."
                : "Opening Recruitment Criteria.";
            step = OperationStep.WaitingForDetails;
            nextStepAt = Environment.TickCount64 + 400;
        }
    }

    private unsafe void AdvanceFromDetails()
    {
        if (GetVisibleConditionAddon() is not null)
        {
            step = OperationStep.ConfiguringCriteria;
            nextStepAt = Environment.TickCount64 + 150;
            return;
        }
        var details = GetVisibleAddon("LookingForGroupDetail");
        if (details is not null && CloseForeignDetails(details)) return;
        if (details is not null && (TryClickPrimaryDetailButton(details) || TryClickButton(details, 109)))
        {
            Status = "Opening the editable recruitment criteria.";
            nextStepAt = Environment.TickCount64 + 400;
            return;
        }
        if (refreshOnly)
        {
            TryOpenActiveRecruitmentDetails();
            Status = "Opening the active recruitment details.";
            nextStepAt = Environment.TickCount64 + 500;
            return;
        }
        step = OperationStep.WaitingForPartyFinder;
        nextStepAt = Environment.TickCount64 + 100;
    }

    private unsafe void ConfigureCriteria()
    {
        var addon = GetVisibleConditionAddon();
        if (addon is null)
            return;
        if (pendingPreset is not null)
        {
            Status = refreshOnly
                ? "Applying preset changes to the active recruitment."
                : "Applying the selected Party Finder preset.";
            WritePreset(pendingPreset, addon);
            if (pendingPreset.DutyCategoryId > 0 && pendingPreset.DutyRowId > 0)
            {
                dutyDropdownAttempts = 0;
                step = OperationStep.PopulatingDutyDropdown;
                nextStepAt = Environment.TickCount64 + 250;
                return;
            }
        }
        step = OperationStep.SubmittingCriteria;
        nextStepAt = Environment.TickCount64 + 450;
    }

    private unsafe void PopulateDutyDropdown()
    {
        var addon = GetVisibleConditionAddon();
        if (addon is null || pendingPreset is null)
            return;
        var dropdown = addon->DutyDropDown;
        if (dropdown is null || dropdown->List is null)
        {
            Fail("The in-game duty list was unavailable, so the preset was not posted.");
            return;
        }
        if (dropdown->List->GetItemCount() > 0)
        {
            step = OperationStep.SelectingDuty;
            nextStepAt = Environment.TickCount64 + 100;
            return;
        }
        if (dutyDropdownAttempts >= 5)
        {
            Fail("The in-game duty list did not load, so the preset was not posted.");
            return;
        }
        dutyDropdownAttempts++;
        Status = $"Loading the in-game duty list ({dutyDropdownAttempts}/5).";
        if (!dropdown->IsOpen)
            ClickDropDownToggle(dropdown);
        nextStepAt = Environment.TickCount64 + 300;
    }

    private unsafe void SelectDuty()
    {
        var addon = GetVisibleConditionAddon();
        if (addon is null || pendingPreset is null)
            return;
        var dropdown = addon->DutyDropDown;
        if (dropdown is null || dropdown->List is null)
            return;
        var selectedIndex = FindDutyIndex(dropdown, pendingPreset.DutyName);
        if (selectedIndex < 0)
        {
            Fail($"'{pendingPreset.DutyName}' was not available in the selected in-game duty category. The preset was not posted.");
            return;
        }

        dropdown->SelectItem(selectedIndex);
        dropdown->List->SelectItem(selectedIndex, true);
        if (dropdown->IsOpen)
            ClickDropDownToggle(dropdown);
        WritePreset(pendingPreset, addon);
        Status = $"Selected {pendingPreset.DutyName}.";
        step = OperationStep.SubmittingCriteria;
        nextStepAt = Environment.TickCount64 + 450;
    }

    private unsafe void SubmitCriteria()
    {
        if (criteriaSubmitted)
        {
            step = OperationStep.Confirming;
            return;
        }
        var addon = GetVisibleConditionAddon();
        if (addon is null)
            return;
        if (pendingPreset is not null)
            WritePreset(pendingPreset, addon);
        if (addon->RecruitMembersButton is null || !addon->RecruitMembersButton->IsEnabled)
            return;
        Status = refreshOnly ? "Applying the preset and refreshing recruitment." : "Posting the Party Finder preset.";
        if (!ClickButton(&addon->AtkUnitBase, addon->RecruitMembersButton))
        {
            Status = "Waiting for the Recruitment Criteria submit control to become ready. Nothing has been submitted.";
            nextStepAt = Environment.TickCount64 + 200;
            return;
        }
        criteriaSubmitted = true;
        confirmationChecks = 0;
        step = OperationStep.Confirming;
        nextStepAt = Environment.TickCount64 + 150;
    }

    private unsafe void ConfirmOrFinish()
    {
        var confirmation = GetVisibleAddon("SelectYesno");
        if (confirmation is not null && TryConfirmComposition(confirmation))
        {
            nextStepAt = Environment.TickCount64 + 200;
            return;
        }
        if (GetVisibleConditionAddon() is not null)
            return;
        if (confirmation is not null)
            return;
        if (confirmationChecks++ < 12)
        {
            nextStepAt = Environment.TickCount64 + 150;
            return;
        }
        if (!IsRecruiting)
        {
            Status = "Waiting for the game to confirm the Party Finder recruitment.";
            nextStepAt = Environment.TickCount64 + 200;
            return;
        }
        Finish(refreshOnly ? $"Party Finder refreshed with preset '{pendingPreset?.Name}'." : $"Party Finder preset '{pendingPreset?.Name}' applied.");
    }

    private unsafe void AdvanceEndRecruitment()
    {
        if (!IsRecruiting)
        {
            Finish("Party Finder recruitment ended.");
            return;
        }

        var criteria = GetVisibleConditionAddon();
        if (criteria is not null)
        {
            criteria->AtkUnitBase.Close(true);
            Status = "Closing Recruitment Criteria before ending the active listing.";
            nextStepAt = Environment.TickCount64 + 300;
            return;
        }

        var details = GetVisibleAddon("LookingForGroupDetail");
        if (details is not null && CloseForeignDetails(details)) return;
        if (details is null)
        {
            TryOpenActiveRecruitmentDetails();
            Status = "Opening the active recruitment details.";
            nextStepAt = Environment.TickCount64 + 500;
            return;
        }

        var endButton = details->GetComponentButtonById(110);
        if (endButton is null || !endButton->IsEnabled)
            return;
        if (!ClickButton(details, endButton)) return;
        Status = "Waiting for confirmation to end recruitment.";
        step = OperationStep.ConfirmingEndRecruitment;
        nextStepAt = Environment.TickCount64 + 150;
    }

    private unsafe void ConfirmEndRecruitment()
    {
        if (!IsRecruiting)
        {
            Finish("Party Finder recruitment ended.");
            return;
        }

        var confirmation = GetVisibleAddon("SelectYesno");
        if (confirmation is null)
        {
            nextStepAt = Environment.TickCount64 + 100;
            return;
        }

        var yesNo = (AddonSelectYesno*)confirmation;
        if (yesNo->YesButton is null || !yesNo->YesButton->IsEnabled)
            return;
        if (!ClickButton(confirmation, yesNo->YesButton)) return;
        Status = "Ending the active Party Finder recruitment.";
        nextStepAt = Environment.TickCount64 + 200;
    }

    private void Finish(string message)
    {
        step = OperationStep.Idle;
        pendingPreset = null;
        refreshOnly = false;
        endingRecruitment = false;
        Status = message;
        notification = message;
        RefreshScheduleChanged();
    }

    private void Fail(string message)
    {
        step = OperationStep.Idle;
        pendingPreset = null;
        refreshOnly = false;
        endingRecruitment = false;
        Status = message;
        notification = message;
        DalamudServices.ChatGui.PrintError($"PartyRefresh: {message}");
        RefreshScheduleChanged();
    }

    private void RejectStart(string message)
    {
        // A duplicate request must not cancel/reset an operation in progress.
        if (IsBusy) notification = message;
        else Fail(message);
    }

    private unsafe void RecoverOperation()
    {
        operationDeadline = Environment.TickCount64 + 20_000;
        nextStepAt = Environment.TickCount64 + 300;

        if (endingRecruitment)
        {
            if (!IsRecruiting)
            {
                Finish("Party Finder recruitment ended.");
                return;
            }
            step = OperationStep.WaitingToEndRecruitment;
            Status = "Reopening the active recruitment details and continuing the request.";
            TryOpenActiveRecruitmentDetails();
            nextStepAt = Environment.TickCount64 + 500;
            return;
        }

        var condition = GetVisibleConditionAddon();
        if (step == OperationStep.Confirming &&
            condition is null &&
            GetVisibleAddon("SelectYesno") is null &&
            IsRecruiting)
        {
            Finish(refreshOnly ? $"Party Finder refreshed with preset '{pendingPreset?.Name}'." : $"Party Finder preset '{pendingPreset?.Name}' applied.");
            return;
        }

        if (criteriaSubmitted)
        {
            Fail("The submitted Party Finder change could not be confirmed. Check the listing before trying again; it was not submitted a second time.");
            return;
        }

        if (condition is not null)
        {
            step = step == OperationStep.Confirming
                ? OperationStep.SubmittingCriteria
                : OperationStep.ConfiguringCriteria;
            Status = "The Party Finder window changed state; continuing from Recruitment Criteria.";
            return;
        }

        if (refreshOnly)
        {
            step = OperationStep.WaitingForDetails;
            Status = "Reopening the active recruitment details and continuing the refresh.";
            TryOpenActiveRecruitmentDetails();
            nextStepAt = Environment.TickCount64 + 500;
            return;
        }

        if (GetVisibleAddon("LookingForGroupDetail") is not null ||
            GetVisibleAddon("LookingForGroup") is not null)
        {
            step = OperationStep.WaitingForPartyFinder;
            Status = "The Party Finder window changed state; continuing from the open window.";
            return;
        }

        step = OperationStep.WaitingForPartyFinder;
        Status = "Reopening Party Finder and continuing the request.";
        ExecuteCommand("/partyfinder");
        nextStepAt = Environment.TickCount64 + 450;
    }

    private unsafe bool TryOpenActiveRecruitmentDetails()
    {
        var agent = AgentLookingForGroup.Instance();
        var contentId = DalamudServices.PlayerState.ContentId;
        if (agent is null || contentId == 0 || openActiveRecruitment is null)
            return false;
        openActiveRecruitment(agent, contentId);
        return true;
    }

    private unsafe bool CloseForeignDetails(AtkUnitBase* details)
    {
        var agent = AgentLookingForGroup.Instance();
        var localId = DalamudServices.PlayerState.ContentId;
        if (agent is not null && localId != 0 && agent->LastViewedListing.LeaderContentId == localId) return false;
        // Never treat another player's Join button as our listing's Edit button.
        details->Close(true);
        Status = "Closing another player's Party Finder details before reopening your recruitment.";
        nextStepAt = Environment.TickCount64 + 300;
        return true;
    }

    private unsafe void WritePreset(PartyFinderPreset preset, AddonLookingForGroupCondition* addon)
    {
        preset.Normalize();
        var agent = AgentLookingForGroup.Instance();
        if (agent is null)
            throw new InvalidOperationException("Party Finder agent is unavailable.");
        agent->SearchAreaTab = 0;
        agent->GroupTypeTab = (byte)preset.RecruitmentType;
        var recruitment = &agent->StoredRecruitmentInfo;
        recruitment->SelectedCategory = preset.DutyCategoryId == 0
            ? AgentLookingForGroup.DutyCategory.None
            : (AgentLookingForGroup.DutyCategory)(1u << preset.DutyCategoryId);
        recruitment->SelectedDutyId = preset.DutyCategoryId == 0
            ? (ushort)0
            : (ushort)Math.Min(preset.DutyRowId, ushort.MaxValue);
        *((byte*)recruitment + SpecificDutyFlagOffset) = (byte)(recruitment->SelectedDutyId == 0 ? 0 : 2);
        recruitment->Objective = (AgentLookingForGroup.Objective)(1 << preset.ObjectiveId);
        recruitment->CompletionStatus = !preset.CompletionStatusEnabled
            ? AgentLookingForGroup.CompletionStatus.None
            : preset.CompletionStatusType switch
            {
                0 => AgentLookingForGroup.CompletionStatus.DutyComplete,
                1 => AgentLookingForGroup.CompletionStatus.DutyCompleteWeeklyUnclaimed,
                _ => AgentLookingForGroup.CompletionStatus.DutyIncomplete,
            };
        var dutyFinder = AgentLookingForGroup.DutyFinderSetting.None;
        if (preset.DutyCategoryId > 0)
        {
            if (preset.UnrestrictedParty) dutyFinder |= AgentLookingForGroup.DutyFinderSetting.UnrestrictedParty;
            if (preset.MinimumItemLevel) dutyFinder |= AgentLookingForGroup.DutyFinderSetting.MinimumIL;
            if (preset.SilenceEcho) dutyFinder |= AgentLookingForGroup.DutyFinderSetting.SilenceEcho;
        }
        recruitment->DutyFinderSettingFlags = dutyFinder;
        recruitment->LootRule = (AgentLookingForGroup.LootRule)preset.LootRules;
        recruitment->Password = preset.FormPrivateParty ? (ushort)preset.PrivatePartyPassword : (ushort)10000;
        var languages = (AgentLookingForGroup.Language)0;
        if (preset.Japanese) languages |= AgentLookingForGroup.Language.Japanese;
        if (preset.English) languages |= AgentLookingForGroup.Language.English;
        if (preset.German) languages |= AgentLookingForGroup.Language.German;
        if (preset.French) languages |= AgentLookingForGroup.Language.French;
        recruitment->LanguageFlags = languages;
        recruitment->LimitRecruitingToWorld = (byte)(preset.LimitRecruitingToWorld ? 0 : 1);
        recruitment->OnePlayerPerJob = (byte)(preset.OnePlayerPerJob ? 1 : 0);
        recruitment->NumberOfSlotsInMainParty = 8;
        recruitment->NumberOfGroups = (byte)(preset.RecruitmentType == 1 ? 3 : 1);
        agent->AvgItemLv = (ushort)preset.AvgItemLevel;
        agent->AvgItemLvEnabled = (byte)(preset.AvgItemLevelEnabled ? 1 : 0);

        var comment = Encoding.UTF8.GetBytes(preset.Comment);
        SetFixedBytes((byte*)recruitment + CommentOffset, comment, 192);
        var slots = (ulong*)((byte*)recruitment + SlotFlagsOffset);
        for (var index = 0; index < 48; index++)
            slots[index] = RoleMask(PartyRefreshRole.Free);
        slots[0] = MaskForJob(DalamudServices.PlayerState.ClassJob.RowId);
        for (var index = 1; index < 8; index++)
            slots[index] = RoleMask(preset.Slots[index]);

        if (addon is null)
            return;

        if (addon->CommentTextInput is not null)
            addon->CommentTextInput->SetText(comment);
        SetChecked(addon->CompletionStatusCheckBox, preset.CompletionStatusEnabled);
        SetChecked(addon->BeginnersWelcomeCheckBox, false);
        SetChecked(addon->RecordableCheckBox, false);
        SetChecked(addon->FormPrivatePartyCheckbox, preset.FormPrivateParty);
        if (preset.FormPrivateParty && addon->PasswordNumericInput is not null)
            SetNumeric(addon->PasswordNumericInput, preset.PrivatePartyPassword);
        SetChecked(addon->LimitToWorldServerCheckbox, preset.LimitRecruitingToWorld);
        SetChecked(addon->OnePlayerPerJobCheckbox, preset.OnePlayerPerJob);
        SetChecked(addon->AvgItemLevelCheckbox, preset.AvgItemLevelEnabled);
        if (preset.AvgItemLevelEnabled && addon->AvgItemLevelNumericInput is not null)
            SetNumeric(addon->AvgItemLevelNumericInput, preset.AvgItemLevel);
        SetChecked(addon->UnrestrictedPartyCheckBox, preset.DutyCategoryId > 0 && preset.UnrestrictedParty);
        SetChecked(addon->MinimumItemLevelCheckBox, preset.DutyCategoryId > 0 && preset.MinimumItemLevel);
        SetChecked(addon->SilenceEchoCheckbox, preset.DutyCategoryId > 0 && preset.SilenceEcho);
        SetChecked(addon->RemoveRoleRestrictionsCheckBox, preset.RemoveRoleRestrictions);
        SetChecked(addon->UnselectClassesCheckbox, preset.UnselectClasses);

        var languageCheckboxes = (AtkComponentCheckBox**)((byte*)addon + LanguageCheckboxesOffset);
        SetChecked(languageCheckboxes[0], preset.Japanese);
        SetChecked(languageCheckboxes[1], preset.English);
        SetChecked(languageCheckboxes[2], preset.German);
        SetChecked(languageCheckboxes[3], preset.French);
    }

    private static unsafe void SetChecked(AtkComponentCheckBox* checkbox, bool value)
    {
        if (checkbox is not null)
            checkbox->SetChecked(value);
    }

    private static unsafe void SetNumeric(AtkComponentNumericInput* input, int value)
    {
        input->SetValue(value);
        input->Value = value;
        input->UpdateTextNode();
    }

    private static unsafe void ClickDropDownToggle(AtkComponentDropDownList* dropdown)
    {
        if (dropdown is null || dropdown->Checkbox is null)
            return;
        var owner = dropdown->Checkbox->AtkComponentButton.AtkComponentBase.OwnerNode;
        if (owner is null)
            return;
        var node = &owner->AtkResNode;
        var source = (AtkEvent*)node->AtkEventManager.Event;
        if (source is null)
            return;
        var click = *source;
        click.State.EventType = AtkEventType.ButtonClick;
        click.State.ReturnFlags = 0;
        click.State.StateFlags = 0;
        dropdown->AtkComponentBase.AtkEventListener.ReceiveEvent(AtkEventType.ButtonClick, (int)source->Param, &click);
    }

    private static unsafe int FindDutyIndex(AtkComponentDropDownList* dropdown, string expected)
    {
        if (dropdown is null || dropdown->List is null)
            return -1;
        var normalizedExpected = NormalizeDutyName(expected);
        var count = dropdown->List->GetItemCount();
        for (var index = 0; index < count; index++)
        {
            var label = dropdown->List->GetItemLabel(index);
            if (label.Value is null)
                continue;
            var value = Marshal.PtrToStringUTF8((nint)label.Value) ?? string.Empty;
            if (NormalizeDutyName(value).Equals(normalizedExpected, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return -1;
    }

    private static string NormalizeDutyName(string value) => value
        .Replace("  ", " ")
        .Replace(" (Ultimate)", "(Ultimate)")
        .Replace(" (Extreme)", "(Extreme)")
        .Replace(" (Savage)", "(Savage)")
        .Trim();

    private static ulong RoleMask(PartyRefreshRole role)
    {
        if (role == PartyRefreshRole.Omit)
            return 0;
        ulong mask = 0;
        for (var index = 0; index < CombatJobIds.Length; index++)
        {
            var job = CombatJobIds[index];
            if (role == PartyRefreshRole.Free || RoleContainsJob(role, job))
                mask |= 1UL << (index + 1);
        }
        return mask;
    }

    private static bool RoleContainsJob(PartyRefreshRole role, uint job) => role switch
    {
        PartyRefreshRole.Tank => job is 1 or 3 or 19 or 21 or 32 or 37,
        PartyRefreshRole.Healer => job is 6 or 24 or 28 or 33 or 40,
        PartyRefreshRole.MeleeDps => job is 2 or 4 or 20 or 22 or 29 or 30 or 34 or 39 or 41 or 43,
        PartyRefreshRole.PhysicalRangedDps => job is 5 or 23 or 31 or 38,
        PartyRefreshRole.MagicalRangedDps => job is 7 or 25 or 26 or 27 or 35 or 36 or 42,
        _ => false,
    };

    private static ulong MaskForJob(uint job)
    {
        var index = Array.IndexOf(CombatJobIds, job);
        return index < 0 ? RoleMask(PartyRefreshRole.Free) : 1UL << (index + 1);
    }

    private static unsafe void SetFixedBytes(byte* destination, ReadOnlySpan<byte> bytes, int capacity)
    {
        var length = Math.Min(bytes.Length, capacity - 1);
        for (var index = 0; index < length; index++)
            destination[index] = bytes[index];
        destination[length] = 0;
    }

    private static unsafe AddonLookingForGroupCondition* GetVisibleConditionAddon()
    {
        var addon = GetVisibleAddon("LookingForGroupCondition");
        return (AddonLookingForGroupCondition*)addon;
    }

    private static unsafe AtkUnitBase* GetVisibleAddon(string name)
    {
        var pointer = DalamudServices.GameGui.GetAddonByName(name);
        var addon = (AtkUnitBase*)pointer.Address;
        return addon is not null && addon->IsVisible ? addon : null;
    }

    private static unsafe bool TryConfirmComposition(AtkUnitBase* addon)
    {
        var yesNo = (AddonSelectYesno*)addon;
        var prompt = yesNo->PromptText is null ? string.Empty : yesNo->PromptText->NodeText.ToString();
        if (!string.IsNullOrEmpty(prompt) &&
            !prompt.Contains("party composition", StringComparison.OrdinalIgnoreCase) &&
            !prompt.Contains("cannot carry out", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (yesNo->YesButton is null || !yesNo->YesButton->IsEnabled)
            return false;
        return ClickButton(addon, yesNo->YesButton);
    }

    private static unsafe bool TryClickButton(AtkUnitBase* addon, uint componentId)
    {
        if (addon is null)
            return false;
        var button = addon->GetComponentButtonById(componentId);
        if (button is null || !button->IsEnabled)
            return false;
        return ClickButton(addon, button);
    }

    private static unsafe bool TryClickPrimaryDetailButton(AtkUnitBase* addon)
    {
        if (addon is null)
            return false;
        var detail = (AddonLookingForGroupDetail*)addon;
        if (detail->JoinPartyButton is null || !detail->JoinPartyButton->IsEnabled)
            return false;
        return ClickButton(addon, detail->JoinPartyButton);
    }

    private static unsafe bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon is null || !addon->IsReady || !addon->IsVisible || button is null || !button->IsEnabled)
            return false;
        var owner = button->AtkComponentBase.OwnerNode;
        if (owner is null)
            return false;
        var node = &owner->AtkResNode;
        var clickEvent = (AtkEvent*)node->AtkEventManager.Event;
        if (clickEvent is null)
            return false;
        addon->ReceiveEvent(clickEvent->State.EventType, (int)clickEvent->Param, clickEvent);
        return true;
    }

    private static unsafe bool ClickSyntheticAddonButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon is null || button is null || !button->IsEnabled)
            return false;
        var owner = button->AtkComponentBase.OwnerNode;
        var node = owner is null ? button->AtkComponentBase.AtkResNode : &owner->AtkResNode;
        if (node is null)
            return false;
        var source = (AtkEvent*)node->AtkEventManager.Event;
        var click = source is null
            ? new AtkEvent
            {
                Node = node,
                Target = (AtkEventTarget*)&node->AtkEventTarget,
                Listener = (AtkEventListener*)addon,
                Param = node->NodeId,
            }
            : *source;
        click.State.EventType = AtkEventType.ButtonClick;
        click.State.ReturnFlags = 0;
        click.State.StateFlags = 0;
        addon->ReceiveEvent(
            AtkEventType.ButtonClick,
            source is null ? (int)node->NodeId : (int)source->Param,
            &click);
        return true;
    }

    private unsafe delegate void OpenActiveRecruitmentDelegate(void* agentLookingForGroup, ulong contentId);

    private static unsafe void ExecuteCommand(string command)
    {
        using var value = new Utf8String(command);
        var shell = RaptureShellModule.Instance();
        var ui = UIModule.Instance();
        if (shell is null || ui is null)
            throw new InvalidOperationException("The game command system is unavailable.");
        shell->ExecuteCommandInner(&value, ui);
    }

    private enum OperationStep
    {
        Idle,
        WaitingToOpenPartyFinder,
        WaitingForPartyFinder,
        WaitingForDetails,
        ConfiguringCriteria,
        PopulatingDutyDropdown,
        SelectingDuty,
        SubmittingCriteria,
        Confirming,
        WaitingToEndRecruitment,
        ConfirmingEndRecruitment,
    }
}
