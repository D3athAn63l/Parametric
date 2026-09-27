# Skill Passion Progression: in-game checklist

Use a test save and enable the feature. Disable it while preparing exact level/passion fixtures, then enable it for the action under test. Closing settings intentionally backfills already-qualified pawns.

1. **Natural Minor:** start at learned level 9 / None; gain enough XP to reach 10. Expect Minor.
2. **Natural Major:** start at 19 / Minor; gain XP to reach 20. Expect Major. Repeat from None if using a fixture editor after settings close.
3. **Existing Major:** a low-level pawn already with Major stays Major through learning and reload.
4. **Migration:** save with the feature off and pawns at 15 / None, 20 / None, 20 / Minor, and 20 / Major. Enable at the main menu and reload. Expect Minor, Major, Major, Major. Include a caravan pawn and a pawn in cryptosleep. Reload again: no changes or messages.
5. **Decay and persistence:** after earning Minor/Major, reduce skill below its milestone. Save/reload. Passion remains. If testing removal, account for the existing Overload component's documented removal message.
6. **Toggle:** disable the feature; cross a milestone and generate a high-skill pawn. No upgrades. Re-enable and close settings: currently relevant eligible pawns normalize. Previously earned passion is never removed.
7. **Arrival and scope:** generate a high-skill visitor/raider or bring back a world pawn. Its enabled skills normalize regardless of faction. Incapable skills remain untouched; check for red errors.
8. **Aptitude:** learned 9 with +11 aptitude does not earn passion; learned 20 with negative aptitude earns Major.
9. **UI and regressions:** confirm the localized toggle and scrolling at your UI scale. Confirm the existing Load Support capacities and Overload policy/slowdown/spill still behave as before.

No passion messages are expected. XP gain, daily learning saturation, skill decay and any GM21 mechanics retain their existing behavior.
