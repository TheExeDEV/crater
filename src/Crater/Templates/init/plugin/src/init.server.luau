local toolbar = plugin:CreateToolbar("{{SHORT_NAME}}")
local button = toolbar:CreateButton("{{SHORT_NAME}}", "Run {{SHORT_NAME}}", "rbxassetid://4458901886")

button.ClickableWhenViewportHidden = true

button.Click:Connect(function()
	print("{{SHORT_NAME}} clicked")
end)
