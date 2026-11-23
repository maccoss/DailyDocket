# DailyDocket v26.1.1

A Join button for Zoom, Teams, Meet and Webex meetings, Briefs that stay open, and "not answered" that clears as soon as you respond.

## New Features

- **Join on the Schedule.** A meeting with a Zoom, Teams, Google Meet or Webex link (in Google's
  conference details, the location or the invitation's text) shows **Join**, which opens it. Only
  links to those services become a button, and only the link is kept, not the invitation's text.
  (`src/DailyDocket.Core/Calendar/CalendarParser.cs`)

## Bug Fixes

- An open **Brief** closed by itself within seconds, whenever the window refreshed after a sync
  step. It now stays open until it is closed. (`src/DailyDocket.App/ViewModels/Items.cs`)
- After **Accept**, **Maybe** or **Decline**, the meeting still said "not answered" until the next
  calendar sync, up to 15 minutes later. It now clears as soon as the response is made.
